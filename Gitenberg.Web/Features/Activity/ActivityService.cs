using System.Globalization;
using Gitenberg.Web.Database;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Activity;

public sealed record ActivityDay(string Date, int Count);

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

    public async Task<List<ActivityDay>> GetHeatmapAsync(
        long telegramId,
        int? clientTzOffsetMinutes,
        int days = 371,
        DateOnly? from = null,
        DateOnly? to = null
    )
    {
        var uid = telegramId.ToString(CultureInfo.InvariantCulture);
        var todayLocal = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(-(clientTzOffsetMinutes ?? -180)));
        var startDay = (from ?? todayLocal.AddDays(-(days - 1))).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDay = (to ?? todayLocal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rows = await dbContext.Database.SqlQuery<ActivityDayRow>(
            $"""
            SELECT ActivityDate AS Date, Count AS Count FROM ActivityDays
            WHERE TelegramUserId = {uid} AND ActivityDate >= {startDay} AND ActivityDate <= {endDay}
            ORDER BY ActivityDate
            """
        ).ToListAsync();

        return rows.Select(r => new ActivityDay(r.Date, r.Count)).ToList();
    }

    private sealed record ActivityDayRow(string Date, int Count);
}
