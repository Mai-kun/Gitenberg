using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Gitenberg.Tests.Infrastructure.FakeClasses;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.TelegramBot;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Octokit;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;
using AppUser = Gitenberg.Web.Models.User;
using BotUser = Telegram.Bot.Types.User;

namespace Gitenberg.Tests.Features.TelegramBot;

public class QuickCaptureTests
{
    private readonly TokenEncryptionService _encryptionService;

    public QuickCaptureTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
    }

    private static AppDbContext CreateInMemoryDbContext()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;

        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private UpdateHandler CreateHandler(
        FakeTelegramBotClient botClient,
        AppDbContext dbContext,
        MockGitHubService gitHubService
    )
    {
        var botConfig = new BotConfiguration { HostAddress = "https://example.com" };
        var inlineSearchHandler = new InlineSearchHandler(
            botClient,
            dbContext,
            botConfig,
            new NoteIndexer(dbContext, gitHubService, _encryptionService, new FakeLogger<NoteIndexer>()),
            new InlineFileLinkService(botConfig),
            new FakeLogger<InlineSearchHandler>()
        );
        return new UpdateHandler(
            botClient,
            dbContext,
            botConfig,
            gitHubService,
            _encryptionService,
            inlineSearchHandler,
            new FakeLogger<UpdateHandler>()
        );
    }

    private async Task<AppDbContext> CreateRegisteredUserAsync(string inboxPath = "inbox", string attachmentsPath = "inbox/attachments")
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new AppUser
        {
            TelegramId = 42,
            GitHubToken = _encryptionService.EncryptToken("github_token", TimeSpan.FromDays(1)),
            RepositoryOwner = "octocat",
            RepositoryName = "my-notes",
            InboxPath = inboxPath,
            AttachmentsPath = attachmentsPath,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static Message CreateTextMessage(string text, long telegramId = 42)
    {
        return new Message
        {
            From = new BotUser { Id = telegramId, IsBot = false, FirstName = "Tester" },
            Chat = new Chat { Id = telegramId, Type = ChatType.Private },
            Date = DateTime.UtcNow,
            Text = text,
        };
    }

    private static Message CreatePhotoMessage(string caption, string? sourceTitle, long telegramId = 42)
    {
        var message = new Message
        {
            From = new BotUser { Id = telegramId, IsBot = false, FirstName = "Tester" },
            Chat = new Chat { Id = telegramId, Type = ChatType.Private },
            Date = DateTime.UtcNow,
            Photo = new[]
            {
                new PhotoSize { FileId = "photo-small", Width = 90, Height = 90 },
                new PhotoSize { FileId = "photo-large", Width = 1280, Height = 720 },
            },
            Caption = caption,
        };

        if (sourceTitle != null)
        {
            message.ForwardOrigin = new MessageOriginChannel
            {
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = -100123, Title = sourceTitle, Type = ChatType.Channel },
                MessageId = 77,
            };
        }

        return message;
    }

    // ------------------------------------------------------------------
    // Binary upload through the Octokit content API
    // ------------------------------------------------------------------

    [Fact]
    public async Task UploadBinaryFileAsync_ShouldPassBase64ContentWithConvertFalse_ToOctokitCreateFile()
    {
        // Arrange
        var recorder = FakeOctokitGitHubClient.Create();
        IGitHubClient fakeOctokit = (IGitHubClient)(object)recorder;
        var service = new GitHubService(fakeOctokit);
        var context = new GitHubRepositoryContext("github_token", "octocat", "my-notes");
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };

        // Act
        await service.UploadBinaryFileAsync(context, "inbox/attachments/photo.jpg", bytes, "Add image attachment");

        // Assert
        var created = recorder.CreatedFiles.Should().ContainSingle().Subject;
        created.Owner.Should().Be("octocat");
        created.Repo.Should().Be("my-notes");
        created.Path.Should().Be("inbox/attachments/photo.jpg");
        created.Request.Message.Should().Be("Add image attachment");

        // convertContentToBase64=false: the Base64 string must be stored verbatim.
        // With the default (true) Octokit would Base64-encode it a second time
        // and the round-trip below would fail.
        created.Request.Content.Should().Be(Convert.ToBase64String(bytes));
        Convert.FromBase64String(created.Request.Content).Should().Equal(bytes);
    }

    // ------------------------------------------------------------------
    // Relative image path calculation
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("inbox/note.md", "inbox/attachments/img.jpg", "attachments/img.jpg")]
    [InlineData("inbox/note.md", "inbox/img.jpg", "img.jpg")]
    [InlineData("notes/note.md", "assets/img.jpg", "../assets/img.jpg")]
    [InlineData("a/b/c/note.md", "a/x/y/img.jpg", "../../x/y/img.jpg")]
    [InlineData("note.md", "assets/img.jpg", "assets/img.jpg")]
    [InlineData("/inbox/note.md", "inbox/attachments/img.jpg", "attachments/img.jpg")]
    public void GetRelativeImagePath_ShouldResolvePath_FromNoteToAttachment(
        string notePath,
        string attachmentPath,
        string expected
    )
    {
        // Act
        var relative = QuickCaptureNoteBuilder.GetRelativeImagePath(notePath, attachmentPath);

        // Assert
        relative.Should().Be(expected);
    }

    // ------------------------------------------------------------------
    // Markdown note formatting
    // ------------------------------------------------------------------

    [Fact]
    public void BuildNote_ShouldIncludeSourceAndImage_WhenForwardedPhotoWithCaption()
    {
        // Act
        var content = QuickCaptureNoteBuilder.BuildNote(
            "Пост о сериале",
            "My Channel",
            "attachments/img.jpg",
            new DateTime(2026, 9, 6, 12, 30, 0)
        );

        // Assert
        content.Should().Be(
            "# Заметка от 2026-09-06 12:30\n" +
            "\n" +
            "> 📢 **Источник:** My Channel\n" +
            "\n" +
            "![Изображение](attachments/img.jpg)\n" +
            "\n" +
            "Пост о сериале"
        );
    }

    [Fact]
    public void BuildNote_ShouldOmitSourceAndImage_WhenPlainNote()
    {
        // Act
        var content = QuickCaptureNoteBuilder.BuildNote(
            "просто текст",
            null,
            null,
            new DateTime(2026, 9, 6, 12, 30, 0)
        );

        // Assert
        content.Should().Be("# Заметка от 2026-09-06 12:30\n\nпросто текст");
        content.Should().NotContain("Источник");
        content.Should().NotContain("![");
    }

    // ------------------------------------------------------------------
    // Forward source detection
    // ------------------------------------------------------------------

    [Fact]
    public void GetForwardSource_ShouldReturnChannelTitle_ForChannelForward()
    {
        var message = new Message
        {
            ForwardOrigin = new MessageOriginChannel
            {
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = -100, Title = "My Channel", Type = ChatType.Channel },
            },
        };

        QuickCaptureNoteBuilder.GetForwardSource(message).Should().Be("My Channel");
    }

    [Fact]
    public void GetForwardSource_ShouldReturnHiddenUserName_ForHiddenUserForward()
    {
        var message = new Message
        {
            ForwardOrigin = new MessageOriginHiddenUser { Date = DateTime.UtcNow, SenderUserName = "Аноним" },
        };

        QuickCaptureNoteBuilder.GetForwardSource(message).Should().Be("Аноним");
    }

    [Fact]
    public void GetForwardSource_ShouldReturnSenderName_ForUserForward()
    {
        var message = new Message
        {
            ForwardOrigin = new MessageOriginUser
            {
                Date = DateTime.UtcNow,
                SenderUser = new BotUser { Id = 7, IsBot = false, FirstName = "Иван", LastName = "Петров" },
            },
        };

        QuickCaptureNoteBuilder.GetForwardSource(message).Should().Be("Иван Петров");
    }

    [Fact]
    public void GetForwardSource_ShouldReturnChatTitle_ForChatForward()
    {
        var message = new Message
        {
            ForwardOrigin = new MessageOriginChat
            {
                Date = DateTime.UtcNow,
                SenderChat = new Chat { Id = -100, Title = "Group Chat", Type = ChatType.Group },
            },
        };

        QuickCaptureNoteBuilder.GetForwardSource(message).Should().Be("Group Chat");
    }

    [Fact]
    public void GetForwardSource_ShouldReturnNull_WhenMessageIsNotForwarded()
    {
        QuickCaptureNoteBuilder.GetForwardSource(CreateTextMessage("привет")).Should().BeNull();
    }

    // ------------------------------------------------------------------
    // End-to-end quick capture through UpdateHandler
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleUpdateAsync_ShouldRouteNoteAndPhotoToCustomFolders_WhenFoldersConfigured()
    {
        // Arrange
        var db = await CreateRegisteredUserAsync(inboxPath: "notes/capture", attachmentsPath: "media/photos");
        var botClient = new FakeTelegramBotClient { FileContent = [0x89, 0x50, 0x4E, 0x47] };
        var gitHubService = new MockGitHubService();
        var createdNotes = new List<(string Path, string Content)>();
        gitHubService.CreateOrUpdateNoteFunc = (_, path, content, _) =>
        {
            createdNotes.Add((path, content));
            return Task.CompletedTask;
        };
        var handler = CreateHandler(botClient, db, gitHubService);

        var update = new Update { Message = CreatePhotoMessage("Пост о сериале", "My Channel") };

        // Act
        await handler.HandleUpdateAsync(update, CancellationToken.None);

        // Assert
        var upload = gitHubService.UploadedBinaries.Should().ContainSingle().Subject;
        upload.Path.Should().StartWith("media/photos/").And.EndWith(".jpg");
        upload.Content.Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        upload.CommitMessage.Should().Be("Add image attachment");

        var note = createdNotes.Should().ContainSingle().Subject;
        note.Path.Should().StartWith("notes/capture/").And.EndWith(".md");

        var attachmentName = upload.Path.Split('/').Last();
        note.Content.Should().Contain("# Заметка от ");
        note.Content.Should().Contain("> 📢 **Источник:** My Channel");
        note.Content.Should().Contain($"![Изображение](../../media/photos/{attachmentName})");
        note.Content.Should().Contain("Пост о сериале");
        note.Content.Should().NotContain("media/photos/media");

        botClient.SentMessages.Should().ContainSingle();
        botClient.SentMessages[0].Text.Should().Be($"✅ Сохранено в {note.Path}");
        botClient.SentMessages[0].ButtonText.Should().Be("Открыть заметки");
    }

    [Fact]
    public async Task HandleUpdateAsync_ShouldSaveCaptionlessPhoto_WithImageLinkOnly()
    {
        // Arrange
        var db = await CreateRegisteredUserAsync();
        var botClient = new FakeTelegramBotClient();
        var gitHubService = new MockGitHubService();
        var createdNotes = new List<(string Path, string Content)>();
        gitHubService.CreateOrUpdateNoteFunc = (_, path, content, _) =>
        {
            createdNotes.Add((path, content));
            return Task.CompletedTask;
        };
        var handler = CreateHandler(botClient, db, gitHubService);

        // Act
        await handler.HandleUpdateAsync(
            new Update { Message = CreatePhotoMessage(caption: null!, sourceTitle: null) },
            CancellationToken.None
        );

        // Assert
        var note = createdNotes.Should().ContainSingle().Subject;
        note.Path.Should().StartWith("inbox/").And.EndWith(".md");
        note.Content.Should().Contain("![Изображение](attachments/");
        note.Content.Should().NotContain("Источник");
        note.Content.Should().EndWith(")\n\n");
    }

    [Fact]
    public async Task HandleUpdateAsync_ShouldSavePlainTextNote_WithDefaultFolders_WhenNoPhotoAndNoForward()
    {
        // Arrange
        var db = await CreateRegisteredUserAsync();
        var botClient = new FakeTelegramBotClient();
        var gitHubService = new MockGitHubService();
        var createdNotes = new List<(string Path, string Content)>();
        gitHubService.CreateOrUpdateNoteFunc = (_, path, content, _) =>
        {
            createdNotes.Add((path, content));
            return Task.CompletedTask;
        };
        var handler = CreateHandler(botClient, db, gitHubService);

        // Act
        await handler.HandleUpdateAsync(
            new Update { Message = CreateTextMessage("мысль вслух") },
            CancellationToken.None
        );

        // Assert
        var note = createdNotes.Should().ContainSingle().Subject;
        note.Path.Should().StartWith("inbox/").And.EndWith(".md");
        note.Content.Should().Contain("мысль вслух");
        note.Content.Should().NotContain("![");
        note.Content.Should().NotContain("Источник");

        gitHubService.UploadedBinaries.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleUpdateAsync_ShouldPromptRegistration_WhenUserIsNotRegistered()
    {
        // Arrange
        var db = CreateInMemoryDbContext();
        var botClient = new FakeTelegramBotClient();
        var gitHubService = new MockGitHubService();
        var createdNotes = new List<(string Path, string Content)>();
        gitHubService.CreateOrUpdateNoteFunc = (_, path, content, _) =>
        {
            createdNotes.Add((path, content));
            return Task.CompletedTask;
        };
        var handler = CreateHandler(botClient, db, gitHubService);

        // Act
        await handler.HandleUpdateAsync(
            new Update { Message = CreateTextMessage("привет", telegramId: 999) },
            CancellationToken.None
        );

        // Assert
        botClient.SentMessages.Should().ContainSingle();
        botClient.SentMessages[0].Text.Should().Be("Сначала зарегистрируйтесь с помощью /start");
        createdNotes.Should().BeEmpty();
        gitHubService.UploadedBinaries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/unknown")]
    public async Task HandleUpdateAsync_ShouldNotCaptureCommands(string text)
    {
        // Arrange
        var db = await CreateRegisteredUserAsync();
        var botClient = new FakeTelegramBotClient();
        var gitHubService = new MockGitHubService();
        var createdNotes = new List<(string Path, string Content)>();
        gitHubService.CreateOrUpdateNoteFunc = (_, path, content, _) =>
        {
            createdNotes.Add((path, content));
            return Task.CompletedTask;
        };
        var handler = CreateHandler(botClient, db, gitHubService);

        // Act
        await handler.HandleUpdateAsync(new Update { Message = CreateTextMessage(text) }, CancellationToken.None);

        // Assert
        createdNotes.Should().BeEmpty();
        gitHubService.UploadedBinaries.Should().BeEmpty();
        botClient.SentMessages.Should().BeEmpty();
    }
}
