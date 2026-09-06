using System.Globalization;
using System.Text.RegularExpressions;
using Gitenberg.Web.Database;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Reminders;

public sealed record ReminderMarker(DateTime FireAtUtc, string Text);

public sealed record Reminder(long Id, long TelegramId, int RepositoryId, string? NotePath, string Text, DateTime FireAtUtc);

/// <summary>
/// SQLite-backed reminders. Note markers ("@remind when [text]") are kept in
/// sync with the note content: every save reconciles the pending (unsent)
/// reminders of that note, so erasing a marker cancels the reminder and a
/// changed text updates it. Already-sent reminders are never touched.
/// Reminders are scoped to a repository: identical note paths in different
/// repositories stay independent.
/// </summary>
public partial class ReminderService(AppDbContext dbContext)
{
    public const int MaxAttempts = 5;

    public static void EnsureTableCreated(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS Reminders (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TelegramUserId TEXT NOT NULL,
                RepositoryId TEXT NULL,
                NotePath TEXT NULL,
                Text TEXT NOT NULL,
                FireAt TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                SentAt TEXT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0
            );
            """);
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Reminders_Sent_Fire ON Reminders (SentAt, FireAt);");
    }

    // The RepositoryId column for databases created before multi-repo is added
    // and backfilled by AppDbContext.EnsureRepositoriesTableCreated, because the
    // mapping to a repository row does not exist yet when this method runs.

    // The marker regex only captures the whole tail after "@remind"; splitting
    // "when" from "text" is ReminderParser's job (it must see both tokens of a
    // "2026-09-10 15:00" style date).
    [GeneratedRegex(@"^\s*@remind\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"^#\s+(.+?)\s*$")]
    private static partial Regex HeadingRegex();

    public static List<ReminderMarker> ExtractMarkers(string? content, DateTime? utcNow = null)
    {
        var markers = new List<ReminderMarker>();
        if (string.IsNullOrEmpty(content))
        {
            return markers;
        }

        var seen = new HashSet<DateTime>();
        foreach (var line in content.Split('\n'))
        {
            var match = MarkerRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var parsed = ReminderParser.TryParse(match.Groups[1].Value, utcNow);
            if (parsed.FireAtUtc is not { } fireAt || !seen.Add(fireAt))
            {
                continue;
            }

            markers.Add(new ReminderMarker(fireAt, parsed.Text));
        }
        return markers;
    }

    /// <summary>Display fallback for markers without a text: the first heading or the file name.</summary>
    public static string ExtractNoteTitle(string? content, string notePath)
    {
        if (!string.IsNullOrEmpty(content))
        {
            foreach (var line in content.Split('\n'))
            {
                var heading = HeadingRegex().Match(line);
                if (heading.Success)
                {
                    return heading.Groups[1].Value.Trim();
                }
            }
        }

        var name = notePath.Trim('/').Split('/')[^1];
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }

    public static string FormatLocal(DateTime fireAtUtc) =>
        fireAtUtc.AddHours(3).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Full reconcile of unsent reminders for one note against its new content:
    /// markers that disappeared cancel their reminders, changed texts update,
    /// new markers are scheduled. Fire times are compared by their ISO string.
    /// </summary>
    public async Task UpsertForNoteAsync(long telegramId, int repositoryId, string notePath, string? content)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var normalizedPath = notePath.Trim('/');
        var markers = ExtractMarkers(content);
        var desired = new Dictionary<string, string>();
        foreach (var marker in markers)
        {
            desired[marker.FireAtUtc.ToString("o")] = marker.Text;
        }

        var existing = await dbContext.Database.SqlQuery<ReminderRow>(
            $"""
            SELECT Id, TelegramUserId, RepositoryId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts
            FROM Reminders
            WHERE TelegramUserId = {uid} AND RepositoryId = {rid} AND NotePath = {normalizedPath} AND SentAt IS NULL
            """
        ).ToListAsync();

        foreach (var row in existing.Where(r => !desired.ContainsKey(r.FireAt)))
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Reminders WHERE Id = {row.Id}");
        }

        foreach (var row in existing.Where(r => desired.TryGetValue(r.FireAt, out var text) && r.Text != text))
        {
            var newText = desired[row.FireAt];
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE Reminders SET Text = {newText} WHERE Id = {row.Id}");
        }

        var existingFireAts = existing.Select(r => r.FireAt).ToHashSet();
        foreach (var (fireAt, text) in desired)
        {
            if (existingFireAts.Contains(fireAt))
            {
                continue;
            }

            var displayText = string.IsNullOrWhiteSpace(text) ? ExtractNoteTitle(content, normalizedPath) : text;
            var createdAt = DateTime.UtcNow.ToString("o");
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Reminders (TelegramUserId, RepositoryId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts) VALUES ({uid}, {rid}, {normalizedPath}, {displayText}, {fireAt}, {createdAt}, NULL, 0)"
            );
        }
    }

    public async Task RemoveForNoteAsync(long telegramId, int repositoryId, string notePath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var normalizedPath = notePath.Trim('/');
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM Reminders WHERE TelegramUserId = {uid} AND RepositoryId = {rid} AND NotePath = {normalizedPath} AND SentAt IS NULL"
        );
    }

    public async Task ReassignNoteAsync(long telegramId, int repositoryId, string fromPath, string toPath)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        var from = fromPath.Trim('/');
        var to = toPath.Trim('/');
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Reminders SET NotePath = {to} WHERE TelegramUserId = {uid} AND RepositoryId = {rid} AND NotePath = {from} AND SentAt IS NULL"
        );
    }

    /// <summary>Deletes every unsent reminder of a repository (cascade cleanup on repository removal).</summary>
    public async Task DeleteAllForRepositoryAsync(long telegramId, int repositoryId)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var rid = repositoryId.ToString(CultureInfo.InvariantCulture);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM Reminders WHERE TelegramUserId = {uid} AND RepositoryId = {rid} AND SentAt IS NULL"
        );
    }

    /// <summary>Unsent, due reminders: due now and under the attempt limit.</summary>
    public async Task<List<Reminder>> GetDueAsync(DateTime utcNow)
    {
        var nowIso = utcNow.ToString("o");
        var rows = await dbContext.Database.SqlQuery<ReminderRow>(
            $"""
            SELECT Id, TelegramUserId, RepositoryId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts
            FROM Reminders
            WHERE SentAt IS NULL AND Attempts < {MaxAttempts} AND FireAt <= {nowIso}
            ORDER BY FireAt
            LIMIT 20
            """
        ).ToListAsync();

        return rows.Select(MapReminder).ToList();
    }

    public async Task MarkSentAsync(long id)
    {
        var sentAt = DateTime.UtcNow.ToString("o");
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE Reminders SET SentAt = {sentAt} WHERE Id = {id}");
    }

    public async Task IncrementAttemptsAsync(long id)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE Reminders SET Attempts = Attempts + 1 WHERE Id = {id}");
    }

    private static Reminder MapReminder(ReminderRow row) => new(
        row.Id,
        long.TryParse(row.TelegramUserId, CultureInfo.InvariantCulture, out var telegramId) ? telegramId : 0,
        int.TryParse(row.RepositoryId, CultureInfo.InvariantCulture, out var repositoryId) ? repositoryId : 0,
        row.NotePath,
        row.Text,
        DateTime.TryParse(row.FireAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fireAt) ? fireAt : DateTime.UtcNow
    );

    private sealed record ReminderRow(long Id, string TelegramUserId, string? RepositoryId, string? NotePath, string Text, string FireAt, string CreatedAt, string? SentAt, int Attempts);
}
