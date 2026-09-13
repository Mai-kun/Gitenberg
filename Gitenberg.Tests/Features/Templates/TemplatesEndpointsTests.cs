using System.Collections;
using System.Net;
using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Auth;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.Templates;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;
using Repository = Gitenberg.Web.Models.Repository;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Templates;

public class TemplatesEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly MockGitHubService _gitHubService;
    private readonly IMemoryCache _memoryCache;

    public TemplatesEndpointsTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
        _gitHubService = new MockGitHubService();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    private IRepositoryContextResolver CreateResolver(AppDbContext db) =>
        new RepositoryContextResolver(db, _encryptionService, NullLogger<RepositoryContextResolver>.Instance);

    private static PendingSyncService CreatePendingSync(AppDbContext db)
    {
        PendingSyncService.EnsureTableCreated(db);
        return new PendingSyncService(db);
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
        return dbContext;
    }

    private async Task CreateUserAsync(AppDbContext db, long telegramId, string? token = "pat_123")
    {
        var encryptedToken = token == null ? null : _encryptionService.EncryptToken(token, TimeSpan.FromMinutes(10));
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
            GitHubToken = encryptedToken,
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();
    }

    // Octokit 14 exposes Content as a get-only value decoded from the base64
    // EncodedContent, so test files are constructed with encoded payloads.
    private static RepositoryContent CreateContent(
        string name,
        string path,
        ContentType type,
        string? content,
        int size = 100
    )
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
                "sha" => "sha_" + name,
                "size" => size,
                "type" => type,
                "downloadurl" => "http://download",
                "htmlurl" => "http://html",
                "url" => "http://dummy/url",
                "giturl" => "http://dummy/giturl",
                "encoding" => content == null ? null : "base64",
                "encodedcontent" => content == null
                    ? null
                    : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content)),
                _ => null,
            };
        }

        return (RepositoryContent)constructor.Invoke(args);
    }

    private static List<(string Name, string Path, string Content)> GetTemplates(object? value)
    {
        var items = ((IEnumerable)value!).Cast<object>().ToList();
        return items
            .Select(o => (
                Name: (string)o.GetType().GetProperty("Name")!.GetValue(o)!,
                Path: (string)o.GetType().GetProperty("Path")!.GetValue(o)!,
                Content: (string)o.GetType().GetProperty("Content")!.GetValue(o)!
            ))
            .ToList();
    }

    [Fact]
    public async Task ListTemplates_ShouldReturnBadRequest_WhenUserIsNotAuthenticated()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ListTemplates_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, CreatePendingSync(db), httpContext: AuthenticatedContext(12345));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ListTemplates_ShouldReturnBadRequest_WhenRepositoryIsNotConfigured()
    {
        await using var db = CreateInMemoryDbContext();
        db.Users.Add(new User { TelegramId = 12345, CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, CreatePendingSync(db), httpContext: AuthenticatedContext(12345));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ListTemplates_ShouldReturnEmptyList_WhenTemplatesFolderDoesNotExist()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        // GitHub reports a missing folder with a 404 (Octokit NotFoundException).
        _gitHubService.GetNotesFunc = (_, _) => throw new NotFoundException("Not Found", HttpStatusCode.NotFound);

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, CreatePendingSync(db), httpContext: AuthenticatedContext(12345));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var templates = GetTemplates(((IValueHttpResult)result).Value);
        templates.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTemplates_ShouldRequestTemplatesFolderFromGitHub()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        var pathCaptured = (string?)null;
        _gitHubService.GetNotesFunc = (_, path) =>
        {
            pathCaptured = path;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>());
        };

        await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, CreatePendingSync(db), httpContext: AuthenticatedContext(12345));

        pathCaptured.Should().Be(TemplatesEndpoints.TemplatesFolder);
    }

    [Fact]
    public async Task ListTemplates_ShouldReturnOnlyMarkdownFiles_WithNameAndContent()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>
        {
            CreateContent("Встреча.md", "templates/Встреча.md", ContentType.File, "# Встреча {{date}}"),
            CreateContent("notes.txt", "templates/notes.txt", ContentType.File, "not a template"),
            CreateContent("assets", "templates/assets", ContentType.Dir, null),
        });

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, CreatePendingSync(db), httpContext: AuthenticatedContext(12345));

        var templates = GetTemplates(((IValueHttpResult)result).Value);
        templates.Should().HaveCount(1);
        templates[0].Name.Should().Be("Встреча");
        templates[0].Path.Should().Be("templates/Встреча.md");
        templates[0].Content.Should().Be("# Встреча {{date}}");
    }

    [Fact]
    public async Task ListTemplates_ShouldFallBackToContentEndpoint_WhenListingHasNoContent()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        // GitHub omits Content for large files; the endpoint then reads the file itself.
        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>
        {
            CreateContent("Plan.md", "templates/Plan.md", ContentType.File, null),
        });
        _gitHubService.GetNoteContentFunc = (_, path) =>
        {
            path.Should().Be("templates/Plan.md");
            return Task.FromResult<string?>("# Plan");
        };

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, CreatePendingSync(db), httpContext: AuthenticatedContext(12345));

        var templates = GetTemplates(((IValueHttpResult)result).Value);
        templates.Should().HaveCount(1);
        templates[0].Content.Should().Be("# Plan");
    }

    [Fact]
    public async Task ListTemplates_ShouldPreferPendingContent_WhenTemplateIsEditedLocally()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        var pendingSync = CreatePendingSync(db);

        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>
        {
            CreateContent("Встреча.md", "templates/Встреча.md", ContentType.File, "# old remote content"),
        });

        await pendingSync.EnqueueAsync(12345, ActiveRepoId(db, 12345), "save", "templates/Встреча.md", "templates/Встреча.md", "# new local content", null);

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, pendingSync, httpContext: AuthenticatedContext(12345));

        var templates = GetTemplates(((IValueHttpResult)result).Value);
        templates.Should().HaveCount(1);
        templates[0].Content.Should().Be("# new local content");
    }

    [Fact]
    public async Task ListTemplates_ShouldExcludePendingDeletedTemplate()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        var pendingSync = CreatePendingSync(db);

        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>
        {
            CreateContent("Встреча.md", "templates/Встреча.md", ContentType.File, "# Встреча"),
        });

        await pendingSync.EnqueueAsync(12345, ActiveRepoId(db, 12345), "delete", "templates/Встреча.md");

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, pendingSync, httpContext: AuthenticatedContext(12345));

        var templates = GetTemplates(((IValueHttpResult)result).Value);
        templates.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTemplates_ShouldIncludeLocallyCreatedTemplate_BeforeSync()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        var pendingSync = CreatePendingSync(db);

        // The templates folder does not exist on GitHub yet; the new template
        // is a pending local save waiting for the sync timer.
        _gitHubService.GetNotesFunc = (_, _) => throw new NotFoundException("Not Found", HttpStatusCode.NotFound);

        await pendingSync.EnqueueAsync(12345, ActiveRepoId(db, 12345), "save", "templates/Новый.md", "templates/Новый.md", "# Новый шаблон", null);

        var result = await TemplatesEndpoints.ListTemplates(db, CreateResolver(db), _gitHubService, pendingSync, httpContext: AuthenticatedContext(12345));

        var templates = GetTemplates(((IValueHttpResult)result).Value);
        templates.Should().HaveCount(1);
        templates[0].Path.Should().Be("templates/Новый.md");
        templates[0].Content.Should().Be("# Новый шаблон");
    }

    private static HttpContext AuthenticatedContext(long userId)
    {
        var context = new DefaultHttpContext();
        context.Items[CurrentUserId.ItemsKey] = userId;
        return context;
    }

    private static int ActiveRepoId(AppDbContext db, long telegramId) =>
        db.Repositories.Where(r => r.TelegramUserId == telegramId).OrderBy(r => r.Id).First().Id;
}
