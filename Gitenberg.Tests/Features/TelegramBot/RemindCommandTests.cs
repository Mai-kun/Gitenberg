using System.Globalization;
using FluentAssertions;
using Gitenberg.Tests.Infrastructure.FakeClasses;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Activity;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.TelegramBot;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;
using AppUser = Gitenberg.Web.Models.User;
using BotUser = Telegram.Bot.Types.User;

namespace Gitenberg.Tests.Features.TelegramBot;

public class RemindCommandTests
{
    private readonly TokenEncryptionService _encryptionService;

    public RemindCommandTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
    }

    private static AppDbContext CreateInMemoryDbContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;

        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();
        ActivityService.EnsureTableCreated(dbContext);
        ReminderService.EnsureTableCreated(dbContext);
        return dbContext;
    }

    private UpdateHandler CreateHandler(
        FakeTelegramBotClient botClient,
        AppDbContext dbContext,
        MockGitHubService gitHubService)
    {
        var botConfig = new BotConfiguration { HostAddress = "https://example.com" };
        var reminderService = new ReminderService(dbContext);
        var inlineSearchHandler = new InlineSearchHandler(
            botClient,
            dbContext,
            botConfig,
            new RepositoryContextResolver(dbContext, _encryptionService, new FakeLogger<RepositoryContextResolver>()),
            new NoteIndexer(dbContext, gitHubService, _encryptionService, reminderService, new FakeLogger<NoteIndexer>()),
            new InlineFileLinkService(botConfig),
            new FakeLogger<InlineSearchHandler>()
        );
        return new UpdateHandler(
            botClient,
            dbContext,
            botConfig,
            gitHubService,
            new RepositoryContextResolver(dbContext, _encryptionService, new FakeLogger<RepositoryContextResolver>()),
            inlineSearchHandler,
            reminderService,
            new ActivityService(dbContext),
            new FakeLogger<UpdateHandler>()
        );
    }

    private async Task<AppDbContext> CreateRegisteredUserAsync()
    {
        var db = CreateInMemoryDbContext();
        var user = new AppUser
        {
            TelegramId = 42,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var repository = new Gitenberg.Web.Models.Repository
        {
            TelegramUserId = 42,
            DisplayName = "octocat/my-notes",
            RepositoryOwner = "octocat",
            RepositoryName = "my-notes",
            GitHubToken = _encryptionService.EncryptToken("github_token", TimeSpan.FromDays(1)),
            InboxPath = "inbox",
            AttachmentsPath = "inbox/attachments",
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();
        return db;
    }

    private static Message CreateCommand(string text, long telegramId = 42)
    {
        return new Message
        {
            From = new BotUser { Id = telegramId, IsBot = false, FirstName = "Tester" },
            Chat = new Chat { Id = telegramId, Type = ChatType.Private },
            Date = DateTime.UtcNow,
            Text = text,
        };
    }

    [Fact]
    public async Task Remind_TwoTokenDateTime_CreatesNoteSchedulesReminderAndConfirms()
    {
        var db = await CreateRegisteredUserAsync();
        var gitHub = new MockGitHubService();
        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient, db, gitHub);

        await handler.HandleUpdateAsync(new Update { Message = CreateCommand("/remind 2026-09-10 15:00 Купить молоко") }, CancellationToken.None);

        // The note is committed with the marker line inside.
        gitHub.SavedNotes.Should().HaveCount(1);
        var saved = gitHub.SavedNotes[0];
        saved.Path.Should().StartWith("inbox/").And.EndWith("_reminder.md");
        saved.Content.Should().Contain("@remind 2026-09-10 15:00 Купить молоко");

        // The reminder is scheduled at 15:00 local (converted exactly like the parser does).
        var expected = DateTime.SpecifyKind(new DateTime(2026, 9, 10, 15, 0, 0), DateTimeKind.Local).ToUniversalTime();
        var rows = await db.Database.SqlQuery<ReminderRowDto>(
            $"SELECT Id, TelegramUserId, NotePath, Text, FireAt, CreatedAt, SentAt, Attempts FROM Reminders"
        ).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Text.Should().Be("Купить молоко");
        rows[0].NotePath.Should().Be(saved.Path);
        DateTime.Parse(rows[0].FireAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            .Should().Be(expected);

        botClient.SentMessages.Should().Contain(m => m.Text.Contains("Напоминание на 10.09.2026 15:00"));
    }

    [Fact]
    public async Task Remind_InvalidSpec_AnswersWithHelp()
    {
        var db = await CreateRegisteredUserAsync();
        var gitHub = new MockGitHubService();
        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient, db, gitHub);

        await handler.HandleUpdateAsync(new Update { Message = CreateCommand("/remind когда-нибудь") }, CancellationToken.None);

        gitHub.SavedNotes.Should().BeEmpty();
        botClient.SentMessages.Should().Contain(m => m.Text.Contains("Не удалось распознать срок"));
    }

    [Fact]
    public async Task Remind_UnregisteredUser_PromptsRegistration()
    {
        var db = CreateInMemoryDbContext();
        var gitHub = new MockGitHubService();
        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient, db, gitHub);

        await handler.HandleUpdateAsync(new Update { Message = CreateCommand("/remind 2h Дело") }, CancellationToken.None);

        gitHub.SavedNotes.Should().BeEmpty();
        botClient.SentMessages.Should().Contain(m => m.Text.Contains("зарегистрируйтесь"));
    }

    [Fact]
    public async Task QuickCapture_RecordsActivity()
    {
        var db = await CreateRegisteredUserAsync();
        var gitHub = new MockGitHubService();
        var botClient = new FakeTelegramBotClient();
        var handler = CreateHandler(botClient, db, gitHub);

        await handler.HandleUpdateAsync(new Update { Message = CreateCommand("просто заметка") }, CancellationToken.None);

        gitHub.SavedNotes.Should().HaveCount(1);
        var activity = await new ActivityService(db).GetHeatmapAsync(42, -180);
        activity.Should().NotBeEmpty(); // today's counter row exists
    }

    private sealed record ReminderRowDto(long Id, string TelegramUserId, string? NotePath, string Text, string FireAt, string CreatedAt, string? SentAt, int Attempts);
}
