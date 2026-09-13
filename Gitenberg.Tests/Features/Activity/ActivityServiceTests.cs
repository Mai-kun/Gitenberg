using System.Globalization;
using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Activity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gitenberg.Tests.Features.Activity;

public class ActivityServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 6, 22, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;

        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();
        ActivityService.EnsureTableCreated(dbContext);
        return dbContext;
    }

    [Fact]
    public void ResolveDayKey_ClientOffsetShiftsTheDay()
    {
        // 22:00 UTC with UTC+3 client (-180 in JS getTimezoneOffset semantics) → next day.
        ActivityService.ResolveDayKey(-180, NowUtc).Should().Be("2026-09-07");
        // 22:00 UTC with UTC-5 client (+300) → still the same UTC date, 17:00 local.
        ActivityService.ResolveDayKey(300, NowUtc).Should().Be("2026-09-06");
    }

    [Fact]
    public void ResolveDayKey_FallbackIsUtcPlusThree()
    {
        ActivityService.ResolveDayKey(null, NowUtc).Should().Be(ActivityService.ResolveDayKey(-180, NowUtc));
    }

    [Fact]
    public async Task RecordAsync_IncrementsTheSameDay()
    {
        var db = CreateDb();
        var service = new ActivityService(db);

        await service.RecordAsync(42, -180, NowUtc);
        await service.RecordAsync(42, -180, NowUtc);
        await service.RecordAsync(43, -180, NowUtc); // another user does not mix in

        var heatmap = await service.GetHeatmapAsync(42, -180);
        heatmap.Should().ContainSingle(d => d.Date == "2026-09-07" && d.Count == 2);
    }

    [Fact]
    public async Task GetHeatmapAsync_ExcludesDaysOutsideWindow()
    {
        var db = CreateDb();
        var service = new ActivityService(db);
        var uid = "42";

        var recent = DateTime.UtcNow.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ancient = DateTime.UtcNow.AddDays(-400).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ActivityDays (TelegramUserId, ActivityDate, Count) VALUES ({uid}, {recent}, 3)");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ActivityDays (TelegramUserId, ActivityDate, Count) VALUES ({uid}, {ancient}, 9)");

        var heatmap = await service.GetHeatmapAsync(42, -180);

        heatmap.Should().ContainSingle(d => d.Date == recent && d.Count == 3);
    }

    [Fact]
    public async Task GetHeatmapAsync_ExplicitRangeReturnsOnlyDaysInside()
    {
        var db = CreateDb();
        var service = new ActivityService(db);
        var uid = "42";

        const string inRange = "2026-05-10";
        const string afterRange = "2026-05-20";
        const string beforeRange = "2026-04-01";
        foreach (var (day, count) in new[] { (inRange, 2), (afterRange, 4), (beforeRange, 7) })
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO ActivityDays (TelegramUserId, ActivityDate, Count) VALUES ({uid}, {day}, {count})");
        }

        var heatmap = await service.GetHeatmapAsync(
            42, -180, from: new DateOnly(2026, 5, 1), to: new DateOnly(2026, 5, 15));

        heatmap.Should().ContainSingle(d => d.Date == inRange && d.Count == 2);
    }

    [Fact]
    public async Task GetHeatmapAsync_ExplicitRangeMayReachPastTheDefaultWindow()
    {
        var db = CreateDb();
        var service = new ActivityService(db);
        var uid = "42";

        var ancient = DateTime.UtcNow.AddDays(-400).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ActivityDays (TelegramUserId, ActivityDate, Count) VALUES ({uid}, {ancient}, 9)");

        var heatmap = await service.GetHeatmapAsync(42, -180, from: new DateOnly(2020, 1, 1), to: new DateOnly(2030, 1, 1));

        heatmap.Should().ContainSingle(d => d.Date == ancient && d.Count == 9);
    }
}
