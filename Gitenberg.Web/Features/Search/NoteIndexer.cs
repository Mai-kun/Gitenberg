using System.Globalization;
using System.Security.Cryptography;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Octokit;
using Repository = Gitenberg.Web.Models.Repository;

namespace Gitenberg.Web.Features.Search;

/// <summary>
/// Synchronizes the local FTS5 note index with every GitHub repository of
/// every registered user. Only notes whose SHA changed since the last run are
/// downloaded, keeping GitHub API usage minimal.
/// </summary>
public class NoteIndexer(
    AppDbContext dbContext,
    IGitHubService gitHubService,
    ITokenEncryptionService encryptionService,
    ReminderService reminderService,
    ILogger<NoteIndexer> logger
)
{
    public async Task SynchronizeAllUsersAsync(CancellationToken cancellationToken = default)
    {
        var userIds = await dbContext.Users.Select(u => u.TelegramId).ToListAsync(cancellationToken);

        foreach (var telegramId in userIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SynchronizeUserRepositoriesAsync(telegramId, targetRepositoryId: null, cancellationToken);
        }
    }

    /// <summary>
    /// Incremental re-index of a single user's notes. Cheap when nothing
    /// changed (one listing; only notes with a new SHA are downloaded), so
    /// search endpoints can call it right before querying to guarantee
    /// fresh results. With <paramref name="repositoryId"/> only that
    /// repository is refreshed; otherwise all of the user's repositories are.
    /// </summary>
    public async Task SynchronizeUserByIdAsync(long telegramId, int? repositoryId = null, CancellationToken cancellationToken = default)
    {
        var userExists = await dbContext.Users.AnyAsync(u => u.TelegramId == telegramId, cancellationToken);
        if (!userExists) return;
        await SynchronizeUserRepositoriesAsync(telegramId, repositoryId, cancellationToken);
    }

    private async Task SynchronizeUserRepositoriesAsync(long telegramId, int? targetRepositoryId, CancellationToken cancellationToken)
    {
        var query = dbContext.Repositories.Where(r => r.TelegramUserId == telegramId);
        if (targetRepositoryId is { } repositoryId)
        {
            query = query.Where(r => r.Id == repositoryId);
        }

        var repositories = await query.OrderBy(r => r.Id).ToListAsync(cancellationToken);

        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SynchronizeRepositoryAsync(repository, cancellationToken);
        }
    }

    private async Task SynchronizeRepositoryAsync(Repository repository, CancellationToken cancellationToken)
    {
        if (
            string.IsNullOrWhiteSpace(repository.GitHubToken)
            || string.IsNullOrWhiteSpace(repository.RepositoryOwner)
            || string.IsNullOrWhiteSpace(repository.RepositoryName)
        )
        {
            logger.LogWarning(
                "Skipping indexing for repository {RepositoryId} (user {TelegramId}): GitHub settings are not fully configured.",
                repository.Id,
                repository.TelegramUserId
            );
            return;
        }

        GitHubRepositoryContext context;
        try
        {
            context = new GitHubRepositoryContext(
                encryptionService.DecryptToken(repository.GitHubToken),
                repository.RepositoryOwner,
                repository.RepositoryName
            );
        }
        catch (CryptographicException)
        {
            logger.LogWarning(
                "Skipping indexing for repository {RepositoryId} (user {TelegramId}): GitHub token could not be decrypted.",
                repository.Id,
                repository.TelegramUserId
            );
            return;
        }

        try
        {
            await SynchronizeRepositoryNotesAsync(repository, context, cancellationToken);
        }
        catch (ApiException ex)
        {
            logger.LogError(ex, "Failed to fetch notes from GitHub for repository {RepositoryId}.", repository.Id);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Failed to persist the search index for repository {RepositoryId}.", repository.Id);
        }
    }

    private async Task SynchronizeRepositoryNotesAsync(
        Repository repository,
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
            .Where(n => n.TelegramUserId == repository.TelegramUserId && n.RepositoryId == repository.Id)
            .ToDictionaryAsync(n => n.NotePath, cancellationToken);

        // FTS5 columns are TEXT; binding the ids as strings keeps insert/select
        // comparisons type-consistent.
        var userId = repository.TelegramUserId.ToString(CultureInfo.InvariantCulture);
        var repositoryId = repository.Id.ToString(CultureInfo.InvariantCulture);
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
                    "Failed to download note '{NotePath}' for repository {RepositoryId}.",
                    path,
                    repository.Id
                );
                continue;
            }

            if (local is null)
            {
                dbContext.IndexedNotes.Add(
                    new IndexedNote { TelegramUserId = repository.TelegramUserId, RepositoryId = repository.Id, NotePath = path, Sha = sha }
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
                    "Indexed note '{NotePath}' for repository {RepositoryId} has no downloadable content.",
                    path,
                    repository.Id
                );
                updatedCount++;
                continue;
            }

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM NoteSearchFts WHERE TelegramUserId = {userId} AND RepositoryId = {repositoryId} AND NotePath = {path}",
                cancellationToken
            );

            // Index the file name together with the content so search matches
            // note titles too.
            var indexedText = $"{path}\n{content}";
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO NoteSearchFts (TelegramUserId, RepositoryId, NotePath, Content) VALUES ({userId}, {repositoryId}, {path}, {indexedText})",
                cancellationToken
            );

            // External edits can add or remove "@remind" markers — reconcile.
            await reminderService.UpsertForNoteAsync(repository.TelegramUserId, repository.Id, path, content);

            updatedCount++;
        }

        foreach (var path in localNotes.Keys.Where(path => !remoteNotes.ContainsKey(path)))
        {
            dbContext.IndexedNotes.Remove(localNotes[path]);
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM NoteSearchFts WHERE TelegramUserId = {userId} AND RepositoryId = {repositoryId} AND NotePath = {path}",
                cancellationToken
            );
            await reminderService.RemoveForNoteAsync(repository.TelegramUserId, repository.Id, path);
            removedCount++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (updatedCount > 0 || removedCount > 0)
        {
            logger.LogInformation(
                "Search index for repository {RepositoryId} (user {TelegramId}) updated: {UpdatedCount} indexed or updated, {RemovedCount} removed.",
                repository.Id,
                repository.TelegramUserId,
                updatedCount,
                removedCount
            );
        }
    }
}
