using System.Globalization;
using System.Security.Cryptography;
using Gitenberg.Web.Database;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Octokit;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Web.Features.Search;

/// <summary>
/// Synchronizes the local FTS5 index with each registered user's GitHub notes.
/// Only notes whose SHA changed since the last run are downloaded, keeping GitHub API usage minimal.
/// </summary>
public class NoteIndexer(
    AppDbContext dbContext,
    IGitHubService gitHubService,
    ITokenEncryptionService encryptionService,
    ILogger<NoteIndexer> logger
)
{
    public async Task SynchronizeAllUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users.ToListAsync(cancellationToken);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SynchronizeUserAsync(user, cancellationToken);
        }
    }

    /// <summary>
    /// Incremental re-index of a single user's notes. Cheap when nothing
    /// changed (one listing; only notes with a new SHA are downloaded), so
    /// search endpoints can call it right before querying to guarantee
    /// fresh results.
    /// </summary>
    public async Task SynchronizeUserByIdAsync(long telegramId, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, cancellationToken);
        if (user == null) return;
        await SynchronizeUserAsync(user, cancellationToken);
    }

    private async Task SynchronizeUserAsync(User user, CancellationToken cancellationToken)
    {
        if (
            string.IsNullOrWhiteSpace(user.GitHubToken)
            || string.IsNullOrWhiteSpace(user.RepositoryOwner)
            || string.IsNullOrWhiteSpace(user.RepositoryName)
        )
        {
            logger.LogWarning(
                "Skipping indexing for user {TelegramId}: GitHub settings are not fully configured.",
                user.TelegramId
            );
            return;
        }

        GitHubRepositoryContext context;
        try
        {
            context = new GitHubRepositoryContext(
                encryptionService.DecryptToken(user.GitHubToken),
                user.RepositoryOwner,
                user.RepositoryName
            );
        }
        catch (CryptographicException)
        {
            logger.LogWarning(
                "Skipping indexing for user {TelegramId}: GitHub token could not be decrypted.",
                user.TelegramId
            );
            return;
        }

        try
        {
            await SynchronizeUserNotesAsync(user, context, cancellationToken);
        }
        catch (ApiException ex)
        {
            logger.LogError(ex, "Failed to fetch notes from GitHub for user {TelegramId}.", user.TelegramId);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to persist the search index for user {TelegramId}.", user.TelegramId);
        }
    }

    private async Task SynchronizeUserNotesAsync(
        User user,
        GitHubRepositoryContext context,
        CancellationToken cancellationToken
    )
    {
        var remoteNotes = new Dictionary<string, string>();

        // The whole vault must be indexed, not just the repository root:
        // walk every folder breadth-first.
        var queue = new Queue<string?>();
        queue.Enqueue(null);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            foreach (var entry in await gitHubService.GetNotesAsync(context, path))
            {
                if (entry.Type == ContentType.Dir)
                {
                    queue.Enqueue(entry.Path);
                    continue;
                }

                if (
                    entry.Path is null
                    || !entry.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                remoteNotes[entry.Path] = entry.Sha ?? string.Empty;
            }
        }

        var localNotes = await dbContext.IndexedNotes
            .Where(n => n.TelegramUserId == user.TelegramId)
            .ToDictionaryAsync(n => n.NotePath, cancellationToken);

        // FTS5 columns are TEXT; binding the id as a string keeps insert/select comparisons type-consistent.
        var userId = user.TelegramId.ToString(CultureInfo.InvariantCulture);
        var updatedCount = 0;
        var removedCount = 0;

        foreach (var (path, sha) in remoteNotes)
        {
            if (localNotes.TryGetValue(path, out var local) && local.Sha == sha)
            {
                continue;
            }

            string? content;
            try
            {
                content = await gitHubService.GetNoteContentAsync(context, path);
            }
            catch (ApiException ex)
            {
                logger.LogError(
                    ex,
                    "Failed to download note '{NotePath}' for user {TelegramId}.",
                    path,
                    user.TelegramId
                );
                continue;
            }

            if (local is null)
            {
                dbContext.IndexedNotes.Add(
                    new IndexedNote { TelegramUserId = user.TelegramId, NotePath = path, Sha = sha }
                );
            }
            else
            {
                local.Sha = sha;
            }

            if (string.IsNullOrEmpty(content))
            {
                // Octokit returns null content for files larger than 1 MB; keep the SHA so we don't retry every cycle.
                logger.LogWarning(
                    "Indexed note '{NotePath}' for user {TelegramId} has no downloadable content.",
                    path,
                    user.TelegramId
                );
                updatedCount++;
                continue;
            }

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM NoteSearchFts WHERE TelegramUserId = {userId} AND NotePath = {path}",
                cancellationToken
            );

            // Index the file name together with the content so search matches
            // note titles too.
            var indexedText = $"{path}\n{content}";
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO NoteSearchFts (TelegramUserId, NotePath, Content) VALUES ({userId}, {path}, {indexedText})",
                cancellationToken
            );

            updatedCount++;
        }

        foreach (var path in localNotes.Keys.Where(path => !remoteNotes.ContainsKey(path)))
        {
            dbContext.IndexedNotes.Remove(localNotes[path]);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM NoteSearchFts WHERE TelegramUserId = {userId} AND NotePath = {path}",
                cancellationToken
            );
            removedCount++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (updatedCount > 0 || removedCount > 0)
        {
            logger.LogInformation(
                "Search index for user {TelegramId} updated: {UpdatedCount} indexed or updated, {RemovedCount} removed.",
                user.TelegramId,
                updatedCount,
                removedCount
            );
        }
    }
}
