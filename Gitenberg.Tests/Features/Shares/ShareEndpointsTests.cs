using System.Net;
using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Shares;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.TelegramBot;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;
using Repository = Gitenberg.Web.Models.Repository;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Shares;

public class ShareEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly MockGitHubService _gitHubService;
    private readonly ShareConfiguration _shareConfig;
    private readonly BotConfiguration _botConfig;

    public ShareEndpointsTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
        _gitHubService = new MockGitHubService();
        _shareConfig = new ShareConfiguration();
        _botConfig = new BotConfiguration { HostAddress = "https://gitenberg.example" };
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
        PendingSyncService.EnsureTableCreated(dbContext);
        ShareLinksService.EnsureTableCreated(dbContext);
        return dbContext;
    }

    private IRepositoryContextResolver CreateResolver(AppDbContext db) =>
        new RepositoryContextResolver(db, _encryptionService, NullLogger<RepositoryContextResolver>.Instance);

    private static ShareLinksService CreateShareLinks(AppDbContext db)
    {
        ShareLinksService.EnsureTableCreated(db);
        return new ShareLinksService(db);
    }

    private async Task<User> CreateUserAsync(AppDbContext db, long telegramId, string token = "pat_123")
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
            GitHubToken = _encryptionService.EncryptToken(token, TimeSpan.FromMinutes(10)),
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();
        return user;
    }

    // Anonymous-typed endpoint payloads: read properties via reflection.
    private static object? Prop(object? value, string name) =>
        value?.GetType().GetProperty(name)?.GetValue(value);

    private static string? PropString(object? value, string name) => Prop(value, name)?.ToString();

    private static int StatusCodeOf(IResult result) =>
        (result as IStatusCodeHttpResult)?.StatusCode ?? 0;

    // -------------------------------------------------------------------
    // CreateShareLink (authenticated)
    // -------------------------------------------------------------------

    [Fact]
    public async Task CreateShareLink_ShouldReturnBadRequest_WhenTelegramIdIsNull()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await ShareEndpoints.CreateShareLink(
            new ShareNoteRequest("notes/a.md"), null, null, db, CreateResolver(db), _gitHubService,
            new PendingSyncService(db), CreateShareLinks(db), _shareConfig, _botConfig);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task CreateShareLink_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        var result = await ShareEndpoints.CreateShareLink(
            new ShareNoteRequest("   "), null, 12345, db, CreateResolver(db), _gitHubService,
            new PendingSyncService(db), CreateShareLinks(db), _shareConfig, _botConfig);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task CreateShareLink_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await ShareEndpoints.CreateShareLink(
            new ShareNoteRequest("notes/a.md"), null, 12345, db, CreateResolver(db), _gitHubService,
            new PendingSyncService(db), CreateShareLinks(db), _shareConfig, _botConfig);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task CreateShareLink_ShouldReturnNotFound_WhenNoteDoesNotExist()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        _gitHubService.GetNoteContentFunc = (_, _) => throw new NotFoundException("not found", HttpStatusCode.NotFound);

        var result = await ShareEndpoints.CreateShareLink(
            new ShareNoteRequest("notes/missing.md"), null, 12345, db, CreateResolver(db), _gitHubService,
            new PendingSyncService(db), CreateShareLinks(db), _shareConfig, _botConfig);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task CreateShareLink_ShouldReturnOkWithStableUrl()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        _gitHubService.GetNoteContentFunc = (_, _) => Task.FromResult("# Hello");

        var shareLinks = CreateShareLinks(db);
        var first = await ShareEndpoints.CreateShareLink(
            new ShareNoteRequest("notes/hello.md"), null, 12345, db, CreateResolver(db), _gitHubService,
            new PendingSyncService(db), shareLinks, _shareConfig, _botConfig);
        var second = await ShareEndpoints.CreateShareLink(
            new ShareNoteRequest("notes/hello.md"), null, 12345, db, CreateResolver(db), _gitHubService,
            new PendingSyncService(db), shareLinks, _shareConfig, _botConfig);

        StatusCodeOf(first).Should().Be(StatusCodes.Status200OK);
        StatusCodeOf(second).Should().Be(StatusCodes.Status200OK);

        var url = PropString((first as IValueHttpResult)!.Value, "Url");
        var token = PropString((first as IValueHttpResult)!.Value, "Token");
        url.Should().StartWith("https://gitenberg.example/share/").And.EndWith(token);
        token.Should().HaveLength(32);

        // Idempotent: the same note keeps its URL.
        PropString((second as IValueHttpResult)!.Value, "Token").Should().Be(token);
    }

    [Fact]
    public async Task CreateShareLink_ShouldServePendingLocalContent_WithoutTouchingGitHub()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        var pendingSync = new PendingSyncService(db);
        await pendingSync.EnqueueAsync(12345, 1, "save", "notes/draft.md", "notes/draft.md", "local draft");

        var gitHubCalled = false;
        _gitHubService.GetNoteContentFunc = (_, _) =>
        {
            gitHubCalled = true;
            return Task.FromResult("stale remote content");
        };

        var result = await ShareEndpoints.CreateShareLink(
            new ShareNoteRequest("notes/draft.md"), null, 12345, db, CreateResolver(db), _gitHubService,
            pendingSync, CreateShareLinks(db), _shareConfig, _botConfig);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        gitHubCalled.Should().BeFalse("a queued local save is the note the user sees");
    }

    // -------------------------------------------------------------------
    // GetShareLink / RevokeShareLink (authenticated)
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetShareLink_ShouldReturnNotFound_WhenNoLinkExists()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        var result = await ShareEndpoints.GetShareLink(
            "notes/a.md", null, 12345, db, CreateResolver(db), CreateShareLinks(db), _shareConfig, _botConfig);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task RevokeShareLink_ShouldReturnNotFound_WhenTokenBelongsToAnotherUser()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        var shareLinks = CreateShareLinks(db);
        var link = await shareLinks.CreateOrGetAsync(12345, 1, "notes/a.md");

        var result = await ShareEndpoints.RevokeShareLink(link.Token, null, 999, db, shareLinks);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task RevokeShareLink_ShouldStopThePublicUrl()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        var shareLinks = CreateShareLinks(db);
        var link = await shareLinks.CreateOrGetAsync(12345, 1, "notes/a.md");

        var result = await ShareEndpoints.RevokeShareLink(link.Token, null, 12345, db, shareLinks);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        (await shareLinks.GetByTokenAsync(link.Token)).Should().BeNull();
    }

    // -------------------------------------------------------------------
    // GetSharedContent (public — the token is the only credential)
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetSharedContent_ShouldReturnNotFound_ForUnknownToken()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await ShareEndpoints.GetSharedContent(
            "no-such-token", CreateResolver(db), _gitHubService, new PendingSyncService(db), CreateShareLinks(db));

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetSharedContent_ShouldReturnNotFound_AfterRevoke()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        var shareLinks = CreateShareLinks(db);
        var link = await shareLinks.CreateOrGetAsync(12345, 1, "notes/a.md");
        await shareLinks.RevokeAsync(12345, link.Token);

        var result = await ShareEndpoints.GetSharedContent(
            link.Token, CreateResolver(db), _gitHubService, new PendingSyncService(db), shareLinks);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetSharedContent_ShouldReturnTitleAndContent_FromGitHub()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        _gitHubService.GetNoteContentFunc = (ctx, path) =>
        {
            ctx.Token.Should().Be("pat_123");
            path.Should().Be("notes/idea.md");
            return Task.FromResult("# Idea\n\nBody");
        };
        var shareLinks = CreateShareLinks(db);
        var link = await shareLinks.CreateOrGetAsync(12345, 1, "notes/idea.md");

        var result = await ShareEndpoints.GetSharedContent(
            link.Token, CreateResolver(db), _gitHubService, new PendingSyncService(db), shareLinks);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        var value = (result as IValueHttpResult)!.Value;
        PropString(value, "Title").Should().Be("idea", "the title is the file name without folder and extension");
        PropString(value, "Content").Should().Be("# Idea\n\nBody");
    }

    [Fact]
    public async Task GetSharedContent_ShouldReturnNotFound_WhenNoteVanishedFromGitHub()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        _gitHubService.GetNoteContentFunc = (_, _) => throw new NotFoundException("not found", HttpStatusCode.NotFound);
        var shareLinks = CreateShareLinks(db);
        var link = await shareLinks.CreateOrGetAsync(12345, 1, "notes/deleted.md");

        var result = await ShareEndpoints.GetSharedContent(
            link.Token, CreateResolver(db), _gitHubService, new PendingSyncService(db), shareLinks);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetSharedContent_ShouldServePendingLocalContent()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        var pendingSync = new PendingSyncService(db);
        await pendingSync.EnqueueAsync(12345, 1, "save", "notes/a.md", "notes/a.md", "fresh local version");

        var shareLinks = CreateShareLinks(db);
        var link = await shareLinks.CreateOrGetAsync(12345, 1, "notes/a.md");

        var result = await ShareEndpoints.GetSharedContent(
            link.Token, CreateResolver(db), _gitHubService, pendingSync, shareLinks);

        StatusCodeOf(result).Should().Be(StatusCodes.Status200OK);
        PropString((result as IValueHttpResult)!.Value, "Content")
            .Should().Be("fresh local version", "the share shows what the owner sees in the app");
    }

    [Fact]
    public async Task GetSharedContent_ShouldUseTheStoredRepository_NotTheActiveOne()
    {
        await using var db = CreateInMemoryDbContext();
        var user = await CreateUserAsync(db, 12345, "pat_first");

        // A second repository; the user switches to it afterwards.
        var second = new Repository
        {
            TelegramUserId = 12345,
            DisplayName = "owner/second",
            RepositoryOwner = "owner",
            RepositoryName = "second",
            GitHubToken = _encryptionService.EncryptToken("pat_second", TimeSpan.FromMinutes(10)),
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(second);
        await db.SaveChangesAsync();
        user.SelectedRepositoryId = second.Id;
        await db.SaveChangesAsync();

        string? usedToken = null;
        _gitHubService.GetNoteContentFunc = (ctx, _) =>
        {
            usedToken = ctx.Token;
            return Task.FromResult("content");
        };

        var shareLinks = CreateShareLinks(db);
        var link = await shareLinks.CreateOrGetAsync(12345, 1, "notes/a.md");
        await ShareEndpoints.GetSharedContent(
            link.Token, CreateResolver(db), _gitHubService, new PendingSyncService(db), shareLinks);

        usedToken.Should().Be("pat_first", "a share link is pinned to the repository it was created for");
    }

    // -------------------------------------------------------------------
    // Public page
    // -------------------------------------------------------------------

    [Fact]
    public async Task ServeSharePage_ShouldServeThePageContent()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"gitenberg-share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "share.html"), "<html>share</html>");
        try
        {
            // Results.File("share.html") resolves the path via the web-root
            // file provider of the request's IWebHostEnvironment.
            var services = new ServiceCollection()
                           .AddLogging()
                           .AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment
                           {
                               WebRootPath = webRoot,
                               WebRootFileProvider = new PhysicalFileProvider(webRoot),
                           })
                           .BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Response.Body = new MemoryStream();

            await ShareEndpoints.ServeSharePage().ExecuteAsync(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.ContentType.Should().Be("text/html");
            context.Response.Body.Position = 0; // written to the end by the result executor
            using var reader = new StreamReader(context.Response.Body);
            reader.ReadToEnd().Should().Contain("<html>share</html>");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Gitenberg.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
