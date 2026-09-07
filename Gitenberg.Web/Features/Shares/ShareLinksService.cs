using System.Globalization;
using System.Security.Cryptography;
using Gitenberg.Web.Database;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Shares;

public sealed record ShareLink(string Token, long TelegramUserId, int RepositoryId, string NotePath, DateTime CreatedAt);

/// <summary>
/// Public "share web view" links: a persistent random token mapping a note
/// (telegram user + repository + path) to a read-only web page. Kept in SQLite
/// (not in the repository) because the token is a server-side secret; raw SQL
/// instead of an EF entity, following the per-feature table pattern of
/// PinnedItems/Reminders/PendingNoteOps. Paths are stored trimmed of slashes.
/// </summary>
public class ShareLinksService(AppDbContext dbContext)
{
    public static void EnsureTableCreated(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS NoteShareLinks (
                Token          TEXT PRIMARY KEY,
                TelegramUserId TEXT NOT NULL,
                RepositoryId   TEXT NOT NULL,
                NotePath       TEXT NOT NULL,
                CreatedAt      TEXT NOT NULL,
                RevokedAt      TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_NoteShareLinks_Owner
                ON NoteShareLinks (TelegramUserId, RepositoryId, NotePath);
            """);
    }

    /// <summary>
    /// Returns the active (not revoked) share link of the note, creating one
    /// when absent. Idempotent: repeated calls for the same note hand out the
    /// same token, so the URL stays stable across editor sessions.
    /// </summary>
    public async Task<ShareLink> CreateOrGetAsync(long telegramId, int repositoryId, string notePath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var path = NormalizePath(notePath);

        var existing = await QueryActiveAsync(uid, rid, path);
        if (existing != null)
        {
            return existing;
        }

        // The token is the only secret guarding the note: 128 bits of entropy,
        // unguessable and outside any enumeration space. Lowercase hex reads
        // better in a URL (and matches the familiar git-sha look).
        var token = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        var createdAt = DateTime.UtcNow;
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR IGNORE INTO NoteShareLinks (Token, TelegramUserId, RepositoryId, NotePath, CreatedAt, RevokedAt) VALUES ({token}, {uid}, {rid}, {path}, {createdAt.ToString("o")}, NULL)"
        );
        return new ShareLink(token, telegramId, repositoryId, path, createdAt);
    }

    public async Task<ShareLink?> GetActiveAsync(long telegramId, int repositoryId, string notePath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        return await QueryActiveAsync(uid, rid, NormalizePath(notePath));
    }

    /// <summary>Resolves a token handed out earlier; revoked tokens read as missing.</summary>
    public async Task<ShareLink?> GetByTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var row = await dbContext.Database.SqlQuery<ShareLinkRow>(
            $"SELECT Token, TelegramUserId, RepositoryId, NotePath, CreatedAt, RevokedAt FROM NoteShareLinks WHERE Token = {token} AND RevokedAt IS NULL"
        ).FirstOrDefaultAsync();
        return row == null ? null : MapRow(row);
    }

    /// <summary>Revokes the link so its URL stops working immediately. Owner-scoped.</summary>
    public async Task<bool> RevokeAsync(long telegramId, string token)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE NoteShareLinks SET RevokedAt = {DateTime.UtcNow.ToString("o")} WHERE Token = {token} AND TelegramUserId = {uid} AND RevokedAt IS NULL"
        );
        return affected > 0;
    }

    // Re-points links after a move: the exact path plus every shared child
    // (renaming a folder drags its shared content along). Same prefix
    // comparison as PinsService — substr instead of LIKE, which is
    // ASCII-case-insensitive and treats '_' as a wildcard.
    public async Task ReassignOnMoveAsync(long telegramId, int repositoryId, string fromPath, string toPath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var from = NormalizePath(fromPath);
        var to = NormalizePath(toPath);
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE NoteShareLinks
            SET NotePath = {to} || SUBSTR(NotePath, {from.Length + 1})
            WHERE TelegramUserId = {uid} AND RepositoryId = {rid}
              AND (NotePath = {from}
                   OR (length(NotePath) > {from.Length + 1}
                       AND substr(NotePath, 1, {from.Length + 1}) = {from + "/"}))
            """);
    }

    // Revokes links of a deleted item plus everything below it (deleting a
    // folder deletes its content).
    public async Task RemoveForPathAsync(long telegramId, int repositoryId, string notePath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var path = NormalizePath(notePath);
        if (path.Length == 0)
        {
            return;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM NoteShareLinks
            WHERE TelegramUserId = {uid} AND RepositoryId = {rid}
              AND (NotePath = {path}
                   OR (length(NotePath) > {path.Length + 1}
                       AND substr(NotePath, 1, {path.Length + 1}) = {path + "/"}))
            """);
    }

    private async Task<ShareLink?> QueryActiveAsync(string uid, string rid, string path)
    {
        var row = await dbContext.Database.SqlQuery<ShareLinkRow>(
            $"SELECT Token, TelegramUserId, RepositoryId, NotePath, CreatedAt, RevokedAt FROM NoteShareLinks WHERE TelegramUserId = {uid} AND RepositoryId = {rid} AND NotePath = {path} AND RevokedAt IS NULL"
        ).FirstOrDefaultAsync();
        return row == null ? null : MapRow(row);
    }

    private static ShareLink MapRow(ShareLinkRow row) => new(
        row.Token,
        long.Parse(row.TelegramUserId, CultureInfo.InvariantCulture),
        int.Parse(row.RepositoryId, CultureInfo.InvariantCulture),
        row.NotePath,
        ParseTimestamp(row.CreatedAt)
    );

    // /notes/idea.md and notes/idea.md must lead to the same link.
    private static string NormalizePath(string path) => path.Trim('/');

    private static DateTime ParseTimestamp(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.UtcNow;

    private sealed record ShareLinkRow(string Token, string TelegramUserId, string RepositoryId, string NotePath, string CreatedAt, string? RevokedAt);
}
