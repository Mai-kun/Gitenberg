using System.Globalization;
using FluentAssertions;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gitenberg.Tests.Features.Reminders;

public class ReminderServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;

        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();
        ReminderService.EnsureTableCreated(dbContext);
        return dbContext;
    }

    private static async Task<List<(long Id, string FireAt, string Text, long TelegramId)>> ReadRowsAsync(AppDbContext db)
    {
        var rows = await db.Database.SqlQuery<ReminderRowDto>(
            $"SELECT Id, TelegramUserId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts FROM Reminders ORDER BY Id"
        ).ToListAsync();
        return rows.Select(r => (r.Id, r.FireAt, r.Text, long.Parse(r.TelegramUserId, CultureInfo.InvariantCulture))).ToList();
    }

    private sealed record ReminderRowDto(long Id, string TelegramUserId, string? NotePath, string Text, string FireAt, string CreatedAt, string? SentAt, int Attempts);

    [Fact]
    public async Task ExtractMarkers_ParsesLines_AndDedupesFireTimes()
    {
        var content = """
            # Заметка

            @remind 2h Позвонить врачу
            какой-то текст
            @remind 2026-09-10 15:00 Купить молоко
            @remind 2h Другое дело
            """;

        var markers = ReminderService.ExtractMarkers(content, NowUtc);

        markers.Should().HaveCount(2);
        markers[0].FireAtUtc.Should().Be(NowUtc.AddHours(2));
        markers[0].Text.Should().Be("Позвонить врачу");
        markers[1].Text.Should().Be("Купить молоко");
    }

    [Fact]
    public async Task UpsertForNote_AddsUpdatesAndCancels()
    {
        var db = CreateDb();
        var service = new ReminderService(db);

        await service.UpsertForNoteAsync(42, 1, "inbox/note.md", "# Дело\n\n@remind 2h Первая\n@remind 3h Вторая");
        var rows = await ReadRowsAsync(db);
        rows.Should().HaveCount(2);

        // One marker disappears, the other changes its text. The survivor's
        // fire time is recomputed from the relative "2h" at call time.
        await service.UpsertForNoteAsync(42, 1, "inbox/note.md", "# Дело\n\n@remind 2h Первая обновлённая");
        rows = await ReadRowsAsync(db);
        rows.Should().HaveCount(1);
        rows[0].Text.Should().Be("Первая обновлённая");
        DateTime.Parse(rows[0].FireAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            .Should().BeCloseTo(DateTime.UtcNow.AddHours(2), precision: TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task UpsertForNote_NeverTouchesSentReminders()
    {
        var db = CreateDb();
        var service = new ReminderService(db);

        await service.UpsertForNoteAsync(42, 1, "inbox/note.md", "@remind 2h Дело");
        var rows = await ReadRowsAsync(db);
        rows.Should().HaveCount(1);
        await service.MarkSentAsync(rows[0].Id);

        // Marker erased after the reminder fired — history stays.
        await service.UpsertForNoteAsync(42, 1, "inbox/note.md", "# Дело без маркера");
        rows = await ReadRowsAsync(db);
        rows.Should().HaveCount(1);
        rows[0].Text.Should().Be("Дело");
    }

    [Fact]
    public async Task UpsertForNote_UsesNoteTitle_WhenMarkerHasNoText()
    {
        var db = CreateDb();
        var service = new ReminderService(db);

        await service.UpsertForNoteAsync(42, 1, "inbox/note.md", "# Купить хлеб\n\n@remind 1h");

        var rows = await ReadRowsAsync(db);
        rows.Should().HaveCount(1);
        rows[0].Text.Should().Be("Купить хлеб");
    }

    [Fact]
    public async Task RemoveAndReassign_WorkOnUnsentOnly()
    {
        var db = CreateDb();
        var service = new ReminderService(db);

        await service.UpsertForNoteAsync(42, 1, "old/note.md", "@remind 1h Раз\n@remind 2h Два");
        var rows = await ReadRowsAsync(db);
        await service.MarkSentAsync(rows[0].Id);

        await service.ReassignNoteAsync(42, 1, "old/note.md", "new/note.md");
        var afterReassign = await ReadRowsAsync(db);
        afterReassign.Single(r => r.Id == rows[1].Id).FireAt.Should().Be(rows[1].FireAt); // still exists

        var paths = await db.Database.SqlQuery<PathDto>(
            $"SELECT NotePath AS Value FROM Reminders WHERE SentAt IS NULL"
        ).ToListAsync();
        paths.Single().Value.Should().Be("new/note.md");

        await service.RemoveForNoteAsync(42, 1, "new/note.md");
        var remaining = await ReadRowsAsync(db);
        remaining.Should().ContainSingle(r => r.Id == rows[0].Id); // sent history survives deletes
    }

    [Fact]
    public async Task GetDueAsync_ReturnsOnlyDueAndUnderAttemptLimit()
    {
        var db = CreateDb();
        var service = new ReminderService(db);

        var past = DateTime.UtcNow.AddMinutes(-5).ToString("o");
        var future = DateTime.UtcNow.AddMinutes(30).ToString("o");
        var uid = "42";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Reminders (TelegramUserId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts) VALUES ({uid}, 'a.md', 'due', {past}, {past}, NULL, 0)");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Reminders (TelegramUserId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts) VALUES ({uid}, 'b.md', 'future', {future}, {past}, NULL, 0)");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Reminders (TelegramUserId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts) VALUES ({uid}, 'c.md', 'gave-up', {past}, {past}, NULL, 5)");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Reminders (TelegramUserId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts) VALUES ({uid}, 'd.md', 'sent', {past}, {past}, {past}, 0)");

        var due = await service.GetDueAsync(DateTime.UtcNow);

        due.Should().ContainSingle(r => r.Text == "due");
    }

    [Fact]
    public async Task FailedDelivery_IncrementsAttempts()
    {
        var db = CreateDb();
        var service = new ReminderService(db);

        await service.UpsertForNoteAsync(42, 1, "a.md", "@remind 1h Дело");
        var rows = await ReadRowsAsync(db);

        await service.IncrementAttemptsAsync(rows[0].Id);
        await service.IncrementAttemptsAsync(rows[0].Id);

        var attempts = await db.Database.SqlQuery<AttemptsDto>(
            $"SELECT Attempts AS Value FROM Reminders WHERE Id = {rows[0].Id}"
        ).SingleAsync();
        attempts.Value.Should().Be(2);
    }

    private sealed record PathDto(string Value);
    private sealed record AttemptsDto(int Value);
}
