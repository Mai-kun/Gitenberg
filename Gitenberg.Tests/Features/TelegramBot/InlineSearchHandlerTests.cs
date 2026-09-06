using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.TelegramBot;
using Gitenberg.Web.Services;
using Gitenberg.Tests.Mocks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineQueryResults;
using Xunit;
using Repository = Gitenberg.Web.Models.Repository;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.TelegramBot;

public class InlineSearchHandlerTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly FakeTelegramBotClient _botClient = new();
    private readonly MockGitHubService _gitHubService = new();
    private readonly BotConfiguration _botConfig = new()
    {
        HostAddress = "https://bot.test",
        SecretToken = "webhook-secret",
    };

    public InlineSearchHandlerTests()
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
        dbContext.EnsureFtsTableCreated();
        ReminderService.EnsureTableCreated(dbContext);
        return dbContext;
    }

    private static RepositoryContent CreateRepositoryContent(string name, string path, string sha)
    {
        var constructor = typeof(RepositoryContent).GetConstructors()
            .First(c => c.GetParameters().Length > 0);
        var parameters = constructor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var nameLower = parameters[i].Name?.ToLowerInvariant();
            args[i] = nameLower switch
            {
                "name" => name,
                "path" => path,
                "sha" => sha,
                "size" => 100,
                "type" => ContentType.File,
                "downloadurl" => "http://dummy/download",
                "htmlurl" => "http://dummy/html",
                "url" => "http://dummy/url",
                "giturl" => "http://dummy/giturl",
                "encoding" => "utf-8",
                "content" => "dummy content",
                _ => null,
            };
        }

        return (RepositoryContent)constructor.Invoke(args);
    }

    private async Task<User> SeedUserAsync(AppDbContext db, long telegramId = 12345)
    {
        var user = new User
        {
            TelegramId = telegramId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var repository = new Repository
        {
            TelegramUserId = telegramId,
            DisplayName = "owner/repo",
            RepositoryOwner = "owner",
            RepositoryName = "repo",
            GitHubToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10)),
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();
        return user;
    }

    private InlineSearchHandler CreateHandler(AppDbContext db)
    {
        var indexer = new NoteIndexer(db, _gitHubService, _encryptionService, new ReminderService(db), NullLogger<NoteIndexer>.Instance);
        var fileLinks = new InlineFileLinkService(_botConfig);
        return new InlineSearchHandler(
            _botClient,
            db,
            _botConfig,
            new RepositoryContextResolver(db, _encryptionService, NullLogger<RepositoryContextResolver>.Instance),
            indexer,
            fileLinks,
            NullLogger<InlineSearchHandler>.Instance
        );
    }

    private static InlineQuery CreateInlineQuery(string query, long telegramId = 12345) => new()
    {
        Id = "inline-query-id",
        From = new Telegram.Bot.Types.User { Id = telegramId, IsBot = false, FirstName = "Test" },
        Query = query,
    };

    private AnswerInlineQueryRequest SingleAnswer() =>
        _botClient.AnsweredInlineQueries.Should().ContainSingle().Subject;

    [Fact]
    public async Task HandleInlineQuery_ShouldAnswerWithRegistrationHint_WhenUserIsNotRegistered()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var handler = CreateHandler(db);

        // Act
        await handler.HandleInlineQueryAsync(CreateInlineQuery("kernel", telegramId: 999), CancellationToken.None);

        // Assert
        var answer = SingleAnswer();
        answer.Results.Should().ContainSingle();
        var article = answer.Results.ToArray()[0].Should().BeOfType<InlineQueryResultArticle>().Subject;
        article.InputMessageContent.Should().BeOfType<InputTextMessageContent>()
            .Which.MessageText.Should().Contain("не зарегистрированы");
    }

    [Fact]
    public async Task HandleInlineQuery_ShouldAnswerWithHint_WhenQueryIsEmpty()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db);
        var handler = CreateHandler(db);

        // Act
        await handler.HandleInlineQueryAsync(CreateInlineQuery("  "), CancellationToken.None);

        // Assert
        var answer = SingleAnswer();
        var article = answer.Results.ToArray()[0].Should().BeOfType<InlineQueryResultArticle>().Subject;
        article.Title.Should().Be("Поиск по заметкам");
    }

    [Fact]
    public async Task HandleInlineQuery_ShouldReturnTextAndDocumentResults_WithASignedZipLink()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db);

        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(
            [CreateRepositoryContent("kernel.md", "notes/kernel.md", "sha1")]
        );
        _gitHubService.GetNoteContentFunc = (_, _) => Task.FromResult("Заметка про kernel scheduling.");

        var handler = CreateHandler(db);

        // Act
        await handler.HandleInlineQueryAsync(CreateInlineQuery("kernel"), CancellationToken.None);

        // Assert
        var answer = SingleAnswer();
        answer.Results.Should().HaveCount(2);

        var article = answer.Results.ToArray()[0].Should().BeOfType<InlineQueryResultArticle>().Subject;
        article.Title.Should().Be("kernel");
        article.InputMessageContent.Should().BeOfType<InputTextMessageContent>()
            .Which.MessageText.Should().Contain("notes/kernel.md")
            .And.Contain("Заметка про kernel scheduling.");

        // The text result keeps only the GitHub button; the file is delivered
        // as a separate document result instead of a download link.
        var articleButtons = article.ReplyMarkup!.InlineKeyboard.SelectMany(row => row).ToList();
        articleButtons.Should().ContainSingle().Which.Text.Should().Be("Открыть на GitHub");

        var document = answer.Results.ToArray()[1].Should().BeOfType<InlineQueryResultDocument>().Subject;
        document.Title.Should().Be("kernel");
        document.MimeType.Should().Be("application/zip");
        document.DocumentUrl.Should().StartWith("https://bot.test/api/bot/inline-file?uid=12345&");

        var queryParams = new Uri(document.DocumentUrl!).Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : string.Empty);
        queryParams["format"].Should().Be("zip");
        new InlineFileLinkService(_botConfig)
            .TryValidate(
                12345,
                Uri.UnescapeDataString(queryParams["path"]),
                long.Parse(queryParams["exp"]),
                "zip",
                queryParams["sig"]
            )
            .Should().BeTrue();
    }

    [Fact]
    public async Task HandleInlineQuery_ShouldReturnNoResults_WhenNothingMatches()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db);

        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(
            [CreateRepositoryContent("recipe.md", "notes/recipe.md", "sha1")]
        );
        _gitHubService.GetNoteContentFunc = (_, _) => Task.FromResult("Совершенно другой текст.");

        var handler = CreateHandler(db);

        // Act
        await handler.HandleInlineQueryAsync(CreateInlineQuery("kernel"), CancellationToken.None);

        // Assert
        SingleAnswer().Results.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInlineQuery_ShouldSanitizeQuotes_AndReturnNoResults()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db);
        var handler = CreateHandler(db);

        // Act — an unbalanced quote must not crash the handler.
        await handler.HandleInlineQueryAsync(CreateInlineQuery("\""), CancellationToken.None);

        // Assert
        SingleAnswer().Results.Should().BeEmpty();
    }
}
