using System.Text;
using FluentAssertions;
using Gitenberg.Tests.Infrastructure.FakeClasses;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Features.TelegramBot;
using Microsoft.Data.Sqlite;
using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gitenberg.Tests.Features.Reminders;

public class ReminderDispatchServiceTests
{
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

    private static (ReminderDispatchService Service, IServiceProvider Provider) CreateService(
        AppDbContext dbContext,
        FakeTelegramBotClient botClient,
        BotConfiguration botConfig)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        services.AddSingleton<ReminderService>();
        services.AddSingleton<ITelegramBotClient>(botClient);
        services.AddSingleton<BotIdentityService>();
        var provider = services.BuildServiceProvider();

        var service = new ReminderDispatchService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            botConfig,
            new FakeLogger<ReminderDispatchService>());
        return (service, provider);
    }

    private async Task SeedUserAsync(AppDbContext db, long telegramId = 42)
    {
        db.Users.Add(new Gitenberg.Web.Models.User
        {
            TelegramId = telegramId,
            GitHubToken = "encrypted-token",
            RepositoryOwner = "owner",
            RepositoryName = "repo",
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task InsertDueReminderAsync(AppDbContext db, string text = "Позвонить врачу", string? notePath = "inbox/note.md", int attempts = 0)
    {
        var fireAt = DateTime.UtcNow.AddMinutes(-1).ToString("o");
        var uid = "42";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Reminders (TelegramUserId, RepositoryId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts) VALUES ({uid}, '1', {notePath}, {text}, {fireAt}, {fireAt}, NULL, {attempts})");
    }

    private static Reminder CreateReminder(string notePath) =>
        new(Id: 1, TelegramId: 42, RepositoryId: 1, NotePath: notePath, Text: "Позвонить врачу", FireAtUtc: DateTime.UtcNow.AddMinutes(-1));

    [Fact]
    public async Task DueReminder_IsSent_AndMarkedSent()
    {
        var db = CreateDb();
        await SeedUserAsync(db);
        await InsertDueReminderAsync(db);
        var botClient = new FakeTelegramBotClient();
        var (service, _) = CreateService(db, botClient, new BotConfiguration { HostAddress = "https://example.com" });

        await service.DispatchDueAsync(CancellationToken.None);

        botClient.SentMessages.Should().ContainSingle(m => m.ChatId == 42 && m.Text.Contains("Позвонить врачу"));

        var reminderService = new ReminderService(db);
        (await reminderService.GetDueAsync(DateTime.UtcNow)).Should().BeEmpty();
    }

    [Fact]
    public async Task SentMessage_IncludesDeepLink_WhenPayloadFits()
    {
        var db = CreateDb();
        await SeedUserAsync(db);
        await InsertDueReminderAsync(db, notePath: "note.md"); // short path → fits
        var botClient = new FakeTelegramBotClient();
        var (service, _) = CreateService(db, botClient, new BotConfiguration { HostAddress = "https://example.com" });

        await service.DispatchDueAsync(CancellationToken.None);

        var expectedUrl = $"https://t.me/testbot?startapp={DeepLinkBuilder.EncodePayload(1, "note.md")}";
        expectedUrl.Length.Should().BeLessThanOrEqualTo(64 + "https://t.me/testbot?startapp=".Length);
        botClient.SentMessages.Should().ContainSingle();
    }

    [Fact]
    public void Keyboard_LongPath_HasNoStartAppButton_ButKeepsWebAppButton()
    {
        var longPath = "work/projects/2026/quarter3/meeting-notes-very-long-name.md";
        DeepLinkBuilder.BuildNoteUrl("testbot", 1, longPath).Should().BeNull();

        var keyboard = ReminderDispatchService.BuildKeyboard("testbot", CreateReminder(longPath), "https://example.com");
        keyboard.Should().NotBeNull();
        var buttons = keyboard!.InlineKeyboard.SelectMany(r => r).ToList();
        buttons.Should().ContainSingle(b => b.Text == "Открыть приложение");
        buttons.Should().NotContain(b => b.Text == "Открыть заметку");
    }

    [Fact]
    public void Keyboard_ShortPath_ContainsBothButtons()
    {
        var keyboard = ReminderDispatchService.BuildKeyboard("testbot", CreateReminder("note.md"), "https://example.com");

        var buttons = keyboard!.InlineKeyboard.SelectMany(r => r).ToList();
        buttons.Select(b => b.Text).Should().Contain(["Открыть заметку", "Открыть приложение"]);
        buttons.First(b => b.Text == "Открыть заметку").Url.Should().StartWith("https://t.me/testbot?startapp=");
    }

    [Fact]
    public async Task SendFailure_IncrementsAttempts_AndLeavesUnsent()
    {
        var db = CreateDb();
        await SeedUserAsync(db);
        await InsertDueReminderAsync(db);
        var botClient = new FakeTelegramBotClient { ThrowOnSendMessage = true };
        var (service, _) = CreateService(db, botClient, new BotConfiguration { HostAddress = "https://example.com" });

        await service.DispatchDueAsync(CancellationToken.None);

        botClient.SentMessages.Should().BeEmpty();
        var attempts = await db.Database.SqlQuery<AttemptsDto>(
            $"SELECT Attempts AS Value FROM Reminders"
        ).SingleAsync();
        attempts.Value.Should().Be(1);
    }

    [Fact]
    public async Task ReminderWithoutUser_IsRetired()
    {
        var db = CreateDb();
        await InsertDueReminderAsync(db); // no user row
        var botClient = new FakeTelegramBotClient();
        var (service, _) = CreateService(db, botClient, new BotConfiguration { HostAddress = "https://example.com" });

        await service.DispatchDueAsync(CancellationToken.None);

        botClient.SentMessages.Should().BeEmpty();
        (await new ReminderService(db).GetDueAsync(DateTime.UtcNow)).Should().BeEmpty();
    }

    [Fact]
    public void DeepLink_PayloadUsesBase64UrlAlphabetOnly()
    {
        var payload = DeepLinkBuilder.EncodePayload(1, "inbox/заметка.md");
        payload.Should().MatchRegex("^[A-Za-z0-9_-]+$");

        var decoded = Decode(payload);
        decoded.Should().Be("note:1:inbox/заметка.md");
    }

    private static string Decode(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private sealed record AttemptsDto(int Value);
}
