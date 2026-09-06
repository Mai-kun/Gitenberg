using System.Globalization;
using System.Text;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Infrastructure;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace Gitenberg.Web.Features.Sync;

public record PendingOp(long Id, string Kind, string FromPath, string? ToPath, string? Content, DateTime CreatedAt, string? CommitMessage = null);

/// <summary>
/// Local-first write queue: saves, deletes and moves are stored in SQLite and
/// flushed to GitHub either by the background timer or on demand. Read paths
/// (listings and note content) apply pending ops as an overlay so the UI is
/// always consistent with the local state. Ops belong to one repository:
/// switching the active repository never mixes queues.
/// </summary>
public class PendingSyncService(AppDbContext dbContext)
{
    public static void EnsureTableCreated(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS PendingNoteOps (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TelegramUserId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                Kind TEXT NOT NULL,
                FromPath TEXT NOT NULL,
                ToPath TEXT NULL,
                Content TEXT NULL,
                CommitMessage TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """);

        // Databases created before CommitMessage existed: add the column in place.
        var hasCommitMessage = db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM pragma_table_info('PendingNoteOps') WHERE name = 'CommitMessage'"
        ).Single();
        if (hasCommitMessage == 0)
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE PendingNoteOps ADD COLUMN CommitMessage TEXT NULL;");
        }
    }

    public async Task EnqueueAsync(
        long telegramId,
        int repositoryId,
        string kind,
        string fromPath,
        string? toPath = null,
        string? content = null,
        string? commitMessage = null
    )
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO PendingNoteOps (TelegramUserId, RepositoryId, Kind, FromPath, ToPath, Content, CommitMessage, CreatedAt) VALUES ({uid}, {rid}, {kind}, {fromPath.Trim('/')}, {toPath}, {content}, {commitMessage}, {DateTime.UtcNow.ToString("o")})");
    }

    public async Task<List<PendingOp>> GetOpsAsync(long telegramId, int repositoryId)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var rows = await dbContext.Database.SqlQuery<PendingOpRow>(
            $"SELECT Id, Kind, FromPath, ToPath, Content, CommitMessage, CreatedAt FROM PendingNoteOps WHERE TelegramUserId = {uid} AND RepositoryId = {rid} ORDER BY Id"
        ).ToListAsync();
        return rows.Select(r => new PendingOp(
            r.Id, r.Kind, r.FromPath, r.ToPath, r.Content,
            DateTime.TryParse(r.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.UtcNow,
            r.CommitMessage
        )).ToList();
    }

    public async Task<int> GetPendingCountAsync(long telegramId, int repositoryId)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        return await dbContext.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM PendingNoteOps WHERE TelegramUserId = {uid} AND RepositoryId = {rid}"
        ).SingleAsync();
    }

    /// <summary>Total pending ops across all of the user's repositories.</summary>
    public async Task<int> GetPendingCountAllReposAsync(long telegramId)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        return await dbContext.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM PendingNoteOps WHERE TelegramUserId = {uid}"
        ).SingleAsync();
    }

    private async Task DeleteOpAsync(long id)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM PendingNoteOps WHERE Id = {id}");
    }

    private sealed record PendingOpRow(long Id, string Kind, string FromPath, string? ToPath, string? Content, string? CommitMessage, string CreatedAt);

    /// <summary>
    /// Replays the repository's pending ops on GitHub in order. Ops failing with
    /// NotFound are dropped (already gone); any other failure stops the flush
    /// and keeps the remaining ops for the next attempt. Returns the number of
    /// applied ops and the number still pending.
    /// </summary>
    public async Task<(int applied, int remaining)> FlushUserAsync(
        long telegramId,
        int repositoryId,
        GitHubRepositoryContext context,
        IGitHubService gitHubService)
    {
        var ops = await GetOpsAsync(telegramId, repositoryId);
        var applied = 0;

        foreach (var op in ops)
        {
            try
            {
                switch (op.Kind)
                {
                    case "save":
                        await gitHubService.CreateOrUpdateNoteAsync(context, op.FromPath, op.Content ?? string.Empty, op.CommitMessage ?? $"Update note: {op.FromPath}");
                        break;
                    case "delete":
                        try
                        {
                            await gitHubService.DeleteNoteAsync(context, op.FromPath, $"Delete note: {op.FromPath}");
                        }
                        catch (NotFoundException)
                        {
                            // Already gone — the desired end state is reached.
                        }
                        break;
                    case "move":
                        try
                        {
                            await gitHubService.MoveNoteAsync(context, op.FromPath, op.ToPath!, op.Content, $"Move note: {op.FromPath} → {op.ToPath}");
                        }
                        catch (NotFoundException)
                        {
                            // Source vanished (e.g. replayed move) — nothing to do.
                        }
                        break;
                }

                await DeleteOpAsync(op.Id);
                applied++;
            }
            catch (Exception)
            {
                // Stop replaying: later ops depend on this one. Keep the rest queued.
                return (applied, ops.Count - applied);
            }
        }

        return (applied, 0);
    }

    /// <summary>
    /// Deletes every pending op of a repository (cascade cleanup on repository removal).
    /// </summary>
    public async Task DeleteAllForRepositoryAsync(long telegramId, int repositoryId)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM PendingNoteOps WHERE TelegramUserId = {uid} AND RepositoryId = {rid}");
    }

    /// <summary>
    /// Replays pending ops into a virtual delta over the real repository state:
    /// path → (deleted, content). Content is null when the new content is
    /// unknown (a move without payload).
    /// </summary>
    private static Dictionary<string, (bool deleted, string? content)> BuildDelta(List<PendingOp> ops)
    {
        var delta = new Dictionary<string, (bool, string?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case "save":
                    delta[op.FromPath] = (false, op.Content);
                    break;
                case "delete":
                    delta[op.FromPath] = (true, null);
                    break;
                case "move":
                    delta[op.FromPath] = (true, null);
                    delta[op.ToPath!] = (false, op.Content);
                    break;
            }
        }
        return delta;
    }

    /// <summary>
    /// Applies pending changes to a directory listing. Also rewrites the
    /// requested path when it only exists locally under a pending move.
    /// Returns the effective GitHub path that should be listed
    /// (null for the repository root).
    /// </summary>
    public async Task<(string? effectivePath, IReadOnlyList<RepositoryContent> items)> ApplyListOverlayAsync(
        long telegramId, int repositoryId, string? path, Func<string?, Task<IReadOnlyList<RepositoryContent>>> fetch)
    {
        var ops = await GetOpsAsync(telegramId, repositoryId);
        var effectivePath = path;
        var normalized = path?.Trim('/') ?? string.Empty;

        // A pending move may have renamed the folder being requested.
        foreach (var op in ops)
        {
            if (op.Kind == "move" && string.Equals(op.ToPath?.Trim('/'), normalized, StringComparison.OrdinalIgnoreCase))
            {
                effectivePath = op.FromPath;
            }
        }

        var items = await fetch(effectivePath);
        var delta = BuildDelta(ops);
        if (delta.Count == 0) return (effectivePath, items);

        var result = new List<RepositoryContent>();
        foreach (var item in items)
        {
            if (item.Path != null && delta.TryGetValue(item.Path, out var change))
            {
                if (change.deleted) continue;
                // Keep the entry; size may differ from the pending content.
                result.Add(item);
                continue;
            }
            result.Add(item);
        }

        // Locally created paths that GitHub does not know about yet.
        var dirPrefix = string.IsNullOrEmpty(normalized) ? string.Empty : normalized + "/";
        foreach (var (opPath, change) in delta)
        {
            if (change.deleted) continue;
            if (result.Any(r => string.Equals(r.Path, opPath, StringComparison.OrdinalIgnoreCase))) continue;
            if (!string.Equals(opPath.Trim('/'), normalized, StringComparison.OrdinalIgnoreCase)
                && !opPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (opPath.Contains('/'))
            {
                var parent = opPath[..opPath.LastIndexOf('/')];
                if (!string.Equals(parent, normalized, StringComparison.OrdinalIgnoreCase)) continue;
            }
            else if (!string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            var isDir = change.content == null && delta.Any(d => d.Key.StartsWith(opPath + "/", StringComparison.OrdinalIgnoreCase));
            var size = change.content != null ? Encoding.UTF8.GetByteCount(change.content) : 0;
            result.Add(new RepositoryContent(
                Path.GetFileName(opPath), opPath, null, size, isDir ? ContentType.Dir : ContentType.File,
                null, null, null, null, null, null, null, null));
        }

        return (effectivePath, result);
    }

    /// <summary>
    /// Content overlay for a single path: pending save/move provides the
    /// current content; pending deletes and move sources read as missing.
    /// </summary>
    public PendingContentStatus GetContentOverlay(List<PendingOp> ops, string path)
    {
        var status = new PendingContentStatus();
        foreach (var op in ops)
        {
            if (op.Kind == "save" && string.Equals(op.FromPath, path, StringComparison.OrdinalIgnoreCase))
            {
                status.Content = op.Content ?? string.Empty;
                status.Found = true;
            }
            else if (op.Kind == "move" && string.Equals(op.ToPath, path, StringComparison.OrdinalIgnoreCase))
            {
                status.Found = true;
                status.Content = op.Content;
                status.FallbackFromPath = op.Content == null ? op.FromPath : null;
            }
            else if (op.Kind == "delete" && string.Equals(op.FromPath, path, StringComparison.OrdinalIgnoreCase))
            {
                status.Found = false;
                status.Deleted = true;
                status.Content = null;
                status.FallbackFromPath = null;
            }
            else if (op.Kind == "move" && string.Equals(op.FromPath, path, StringComparison.OrdinalIgnoreCase))
            {
                status.Deleted = true;
                status.Found = false;
                status.Content = null;
                status.FallbackFromPath = null;
            }
        }
        return status;
    }

    /// <summary>
    /// Redirects a content read whose path only exists under a pending move.
    /// </summary>
    public async Task<string?> ResolveEffectiveContentPathAsync(long telegramId, int repositoryId, string path)
    {
        var ops = await GetOpsAsync(telegramId, repositoryId);
        var status = GetContentOverlay(ops, path);
        if (status.Deleted) return null;
        if (status.Found && status.Content != null) return null; // content served from the overlay
        if (status.FallbackFromPath != null) return status.FallbackFromPath;
        return path;
    }
}

public class PendingContentStatus
{
    public bool Found { get; set; }
    public bool Deleted { get; set; }
    public string? Content { get; set; }
    public string? FallbackFromPath { get; set; }
}
