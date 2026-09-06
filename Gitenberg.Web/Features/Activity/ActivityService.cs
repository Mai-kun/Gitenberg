using System.Globalization;
using Gitenberg.Web.Database;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Activity;

public sealed record ActivityDay(string Date, int Count);

/// <summary>
/// Daily note-activity counters feeding the GitHub-style heatmap on the Mini
/// App home screen. Events are counted at write time (save/delete/move in the
/// app and quick capture via the bot), not at commit time, so pending
/// local-first changes are visible immediately.
/// </summary>
public class ActivityService(AppDbContext dbContext)
{
    public static void EnsureTableCreated(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS ActivityDays (
                TelegramUserId TEXT NOT NULL,
                ActivityDate TEXT NOT NULL,
                Count INTEGER NOT NULL,
                PRIMARY KEY (TelegramUserId, ActivityDate)
            );
            """);
    }

    /// <summary>
    /// Local day key (yyyy-MM-dd) for an event. Browsers report
    /// Date.getTimezoneOffset() semantics (UTC+3 → -180), so the client value
    /// is negated. Without a browser context (bot quick capture) the RU-user
    /// fallback of UTC+3 is used.
    /// </summary>
    public static string ResolveDayKey(int? clientTzOffsetMinutes, DateTime? utcNow = null)
    {
        var offsetMinutes = clientTzOffsetMinutes ?? -180;
        var localNow = (utcNow ?? DateTime.UtcNow).AddMinutes(-offsetMinutes);
        return localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public async Task RecordAsync(long telegramId, int? clientTzOffsetMinutes, DateTime? utcNow = null)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var day = ResolveDayKey(clientTzOffsetMinutes, utcNow);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ActivityDays (TelegramUserId, ActivityDate, Count)
            VALUES ({uid}, {day}, 1)
            ON CONFLICT(TelegramUserId, ActivityDate) DO UPDATE SET Count = Count + 1
            """);
    }

    /// <summary>
    /// Non-zero days inside the heatmap window (the client fills empty days),
    /// oldest first.
    /// </summary>
    public async Task<List<ActivityDay>> GetHeatmapAsync(long telegramId, int? clientTzOffsetMinutes, int days = 371)
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var todayLocal = DateTime.UtcNow.AddMinutes(-(clientTzOffsetMinutes ?? -180));
        var startDay = todayLocal.AddDays(-(days - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rows = await dbContext.Database.SqlQuery<ActivityDayRow>(
            $"SELECT ActivityDate AS Date, Count AS Count FROM ActivityDays WHERE TelegramUserId = {uid} AND ActivityDate >= {startDay} ORDER BY ActivityDate"
        ).ToListAsync();

        return rows.Select(r => new ActivityDay(r.Date, r.Count)).ToList();
    }

    private sealed record ActivityDayRow(string Date, int Count);
}
