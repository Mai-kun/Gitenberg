using System.Globalization;
using Gitenberg.Web.Database;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Pins;

public sealed record PinnedItem(string ItemPath, DateTime PinnedAt);

/// <summary>
/// Pinned notes and folders ("вынести наверх" markers) shown in the explorer.
/// Kept in SQLite (not in the repository) because they are per-user UI
/// metadata, and scoped per repository so identical paths in different
/// repositories stay independent. Raw SQL instead of an EF entity: ItemPath is
/// part of the primary key, and move-reassignment would otherwise require
/// modifying key values, which tracked entities do not allow.
/// </summary>
public class PinsService(AppDbContext dbContext)
{
    public static void EnsureTableCreated(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS PinnedItems (
                TelegramUserId TEXT NOT NULL,
                RepositoryId   TEXT NOT NULL,
                ItemPath       TEXT NOT NULL,
                PinnedAt       TEXT NOT NULL,
                PRIMARY KEY (TelegramUserId, RepositoryId, ItemPath)
            );
            """);
    }

    public async Task<List<PinnedItem>> ListAsync(long telegramId, int repositoryId)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var rows = await dbContext.Database.SqlQuery<PinRow>(
            $"SELECT ItemPath, PinnedAt FROM PinnedItems WHERE TelegramUserId = {uid} AND RepositoryId = {rid} ORDER BY ItemPath"
        ).ToListAsync();

        return rows
            .Select(r => new PinnedItem(r.ItemPath, ParseTimestamp(r.PinnedAt)))
            .ToList();
    }

    public async Task PinAsync(long telegramId, int repositoryId, string itemPath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var path = itemPath.Trim('/');
        if (path.Length == 0)
        {
            return;
        }

        // Re-pinning refreshes PinnedAt and never duplicates the row.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR REPLACE INTO PinnedItems (TelegramUserId, RepositoryId, ItemPath, PinnedAt) VALUES ({uid}, {rid}, {path}, {DateTime.UtcNow.ToString("o")})"
        );
    }

    public async Task UnpinAsync(long telegramId, int repositoryId, string itemPath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var path = itemPath.Trim('/');
        if (path.Length == 0)
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM PinnedItems WHERE TelegramUserId = {uid} AND RepositoryId = {rid} AND ItemPath = {path}"
        );
    }

    // Re-points pins after a move: the exact path plus every pinned child
    // (renaming a folder drags its pinned content along). A prefix comparison
    // via substr instead of LIKE — LIKE is ASCII-case-insensitive and treats
    // '_' as a wildcard, so "inbox/my_notes/…" would wrongly match
    // "inbox/myXnotes/…".
    public async Task ReassignOnMoveAsync(long telegramId, int repositoryId, string fromPath, string toPath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var from = fromPath.Trim('/');
        var to = toPath.Trim('/');
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE PinnedItems
            SET ItemPath = {to} || SUBSTR(ItemPath, {from.Length + 1})
            WHERE TelegramUserId = {uid} AND RepositoryId = {rid}
              AND (ItemPath = {from}
                   OR (length(ItemPath) > {from.Length + 1}
                       AND substr(ItemPath, 1, {from.Length + 1}) = {from + "/"}))
            """);
    }

    // Drops the pin of a deleted item plus pins of everything below it
    // (deleting a folder deletes its content).
    public async Task RemoveForPathAsync(long telegramId, int repositoryId, string itemPath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var path = itemPath.Trim('/');
        if (path.Length == 0)
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM PinnedItems
            WHERE TelegramUserId = {uid} AND RepositoryId = {rid}
              AND (ItemPath = {path}
                   OR (length(ItemPath) > {path.Length + 1}
                       AND substr(ItemPath, 1, {path.Length + 1}) = {path + "/"}))
            """);
    }

    private static DateTime ParseTimestamp(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.UtcNow;

    private sealed record PinRow(string ItemPath, string PinnedAt);
}
