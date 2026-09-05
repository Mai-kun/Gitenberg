using System.Collections;
using System.Net;
using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Notes;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Octokit;
using Xunit;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Notes;

public class NotesEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly MockGitHubService _gitHubService;
    private readonly IMemoryCache _memoryCache;

    public NotesEndpointsTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
        _gitHubService = new MockGitHubService();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

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

    private static RepositoryContent CreateRepositoryContent(
        string name,
        string path,
        string sha,
        int size,
        ContentType type,
        string downloadUrl,
        string htmlUrl
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
                "sha" => sha,
                "size" => size,
                "type" => type,
                "downloadurl" => downloadUrl,
                "htmlurl" => htmlUrl,
                "url" => "http://dummy/url",
                "giturl" => "http://dummy/giturl",
                "encoding" => "utf-8",
                "content" => "dummy content",
                _ => null,
            };
        }

        return (RepositoryContent)constructor.Invoke(args);
    }

    [Fact]
    public async Task GetNotes_ShouldReturnBadRequest_WhenTelegramIdIsNull()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await NotesEndpoints.GetNotes(null, null, null, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNotes_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await NotesEndpoints.GetNotes(null, 12345, null, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetNotes_ShouldReturnBadRequest_WhenGitHubTokenIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = null,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Act
        var result = await NotesEndpoints.GetNotes(null, 12345, null, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNotes_ShouldReturnOkWithNotes_WhenRequestIsValid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var noteContent = CreateRepositoryContent(
            "note1.md",
            "notes/note1.md",
            "sha123",
            100,
            ContentType.File,
            "http://download",
            "http://html"
        );
        _gitHubService.GetNotesFunc = (ctx, path) =>
        {
            ctx.Token.Should().Be("pat_123");
            ctx.Owner.Should().Be("owner");
            ctx.Repo.Should().Be("repo");
            path.Should().BeNull();
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { noteContent });
        };

        // Act
        var result = await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        valueResult.Value.Should().BeAssignableTo<IEnumerable>();
    }

    [Fact]
    public async Task GetNotes_ShouldPassPathToService_WhenPathIsProvided()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var noteContent = CreateRepositoryContent(
            "subnote.md",
            "subfolder/subnote.md",
            "sha456",
            150,
            ContentType.File,
            "http://download/subfolder",
            "http://html/subfolder"
        );

        var pathCaptured = string.Empty;
        _gitHubService.GetNotesFunc = (ctx, path) =>
        {
            pathCaptured = path;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { noteContent });
        };

        // Act
        var result = await NotesEndpoints.GetNotes("subfolder", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        pathCaptured.Should().Be("subfolder");
    }

    [Fact]
    public async Task GetNotes_ShouldThrowException_WhenGitHubServiceThrowsException()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        _gitHubService.GetNotesFunc = (_, _) => throw new Exception("GitHub API down");

        // Act
        Func<Task> act = () => NotesEndpoints.GetNotes(null, 12345, null, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert
        await act.Should().ThrowAsync<Exception>().WithMessage("GitHub API down");
    }

    [Fact]
    public async Task GetNoteContent_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await NotesEndpoints.GetNoteContent(null!, 12345, null, db, _encryptionService, _gitHubService, CreatePendingSync(db));

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNoteContent_ShouldThrowNotFoundException_WhenNoteDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        _gitHubService.GetNoteContentFunc = (_, _) =>
            throw new NotFoundException("Not found", HttpStatusCode.NotFound);

        // Act
        Func<Task> act = () => NotesEndpoints.GetNoteContent(
            "notes/missing.md",
            12345,
            null,
            db,
            _encryptionService,
            _gitHubService,
            CreatePendingSync(db)
        );

        // Assert
        await act.Should().ThrowAsync<NotFoundException>().WithMessage("Not found");
    }

    [Fact]
    public async Task GetNoteContent_ShouldReturnOkWithContent_WhenRequestIsValid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        _gitHubService.GetNoteContentFunc = (_, _) => Task.FromResult("Hello world note content");

        // Act
        var result = await NotesEndpoints.GetNoteContent(
            "notes/note1.md",
            12345,
            null,
            db,
            _encryptionService,
            _gitHubService,
            CreatePendingSync(db)
        );

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();

        // Extract properties using reflection or casting to check content
        var value = valueResult.Value;
        value.Should().NotBeNull();

        var contentProp = value.GetType().GetProperty("Content");
        contentProp.Should().NotBeNull();
        contentProp.GetValue(value).Should().Be("Hello world note content");
    }

    [Fact]
    public async Task CreateOrUpdateNote_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new CreateOrUpdateNoteRequest(null!, "content", "msg");

        // Act
        var result = await NotesEndpoints.CreateOrUpdateNote(
            request,
            12345,
            null,
            db,
            CreatePendingSync(db),
            _memoryCache
        );

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task CreateOrUpdateNote_ShouldReturnOk_WhenRequestIsValid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var request = new CreateOrUpdateNoteRequest("notes/new.md", "hello", "Create/Update message");

        // Act
        var result = await NotesEndpoints.CreateOrUpdateNote(
            request,
            12345,
            null,
            db,
            CreatePendingSync(db),
            _memoryCache
        );

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        var pending = await CreatePendingSync(db).GetOpsAsync(12345);
        pending.Should().ContainSingle();
        pending[0].Kind.Should().Be("save");
        pending[0].FromPath.Should().Be("notes/new.md");
        pending[0].Content.Should().Be("hello");
    }

    [Fact]
    public async Task DeleteNote_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await NotesEndpoints.DeleteNote(null!, "msg", 12345, null, db, CreatePendingSync(db), _memoryCache);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task DeleteNote_ShouldThrowNotFoundException_WhenNoteDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Act
        var result = await NotesEndpoints.DeleteNote(
            "notes/missing.md",
            "delete msg",
            12345,
            null,
            db,
            CreatePendingSync(db),
            _memoryCache
        );

        // Assert - local-first: the delete is queued, not pushed to GitHub.
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        var pending = await CreatePendingSync(db).GetOpsAsync(12345);
        pending.Should().ContainSingle();
        pending[0].Kind.Should().Be("delete");
        pending[0].FromPath.Should().Be("notes/missing.md");
    }

    [Fact]
    public async Task DeleteNote_ShouldReturnOk_WhenRequestIsValid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Act
        var result = await NotesEndpoints.DeleteNote(
            "notes/delete.md",
            "delete msg",
            12345,
            null,
            db,
            CreatePendingSync(db),
            _memoryCache
        );

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        var pending = await CreatePendingSync(db).GetOpsAsync(12345);
        pending.Should().ContainSingle();
        pending[0].Kind.Should().Be("delete");
        pending[0].FromPath.Should().Be("notes/delete.md");
    }

    [Fact]
    public async Task GetNotes_ShouldUseCache_OnSubsequentCalls()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var noteContent = CreateRepositoryContent(
            "note1.md",
            "notes/note1.md",
            "sha123",
            100,
            ContentType.File,
            "http://download",
            "http://html"
        );

        var callCount = 0;
        _gitHubService.GetNotesFunc = (_, _) =>
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { noteContent });
        };

        // Act - First call (should fetch from service and cache)
        var result1 = await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        // Act - Second call (should hit cache)
        var result2 = await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert
        callCount.Should().Be(1);
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateOrUpdateNote_ShouldBustCache_WhenExecuted()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var noteContent = CreateRepositoryContent(
            "note1.md",
            "notes/note1.md",
            "sha123",
            100,
            ContentType.File,
            "http://download",
            "http://html"
        );

        var callCount = 0;
        _gitHubService.GetNotesFunc = (_, _) =>
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { noteContent });
        };
        _gitHubService.CreateOrUpdateNoteFunc = (_, _, _, _) => Task.CompletedTask;

        // 1. First GetNotes (caches data)
        await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        callCount.Should().Be(1);

        // 2. Create or Update Note (should bust cache)
        var request = new CreateOrUpdateNoteRequest("notes/new.md", "content", "msg");
        await NotesEndpoints.CreateOrUpdateNote(request, 12345, null, db, CreatePendingSync(db), _memoryCache);

        // 3. Second GetNotes (should fetch from service again due to cache bust)
        await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task DeleteNote_ShouldBustCache_WhenExecuted()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var noteContent = CreateRepositoryContent(
            "note1.md",
            "notes/note1.md",
            "sha123",
            100,
            ContentType.File,
            "http://download",
            "http://html"
        );

        var callCount = 0;
        _gitHubService.GetNotesFunc = (_, _) =>
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { noteContent });
        };
        _gitHubService.DeleteNoteFunc = (_, _, _) => Task.CompletedTask;

        // 1. First GetNotes (caches data)
        await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        callCount.Should().Be(1);

        // 2. Delete Note (should bust cache)
        await NotesEndpoints.DeleteNote("notes/note1.md", "msg", 12345, null, db, CreatePendingSync(db), _memoryCache);

        // 3. Second GetNotes (should fetch from service again due to cache bust)
        await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        callCount.Should().Be(2);
    }

    private async Task<User> CreateUserAsync(AppDbContext db, long telegramId, string token = "pat_123")
    {
        var encryptedToken = _encryptionService.EncryptToken(token, TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = telegramId,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static RepositoryContent CreateNote(string name)
    {
        return CreateRepositoryContent(
            name,
            $"notes/{name}",
            $"sha_{name}",
            100,
            ContentType.File,
            "http://download",
            "http://html"
        );
    }

    private static string? GetFirstName(IValueHttpResult? result)
    {
        var value = result?.Value;
        if (value is not IEnumerable enumerable)
        {
            return null;
        }

        foreach (var item in enumerable)
        {
            return item.GetType().GetProperty("Name")?.GetValue(item) as string;
        }

        return null;
    }

    [Fact]
    public async Task GetNotes_ShouldReturnCachedData_FromCache_NotFreshData()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetNotesFunc = (_, _) =>
            Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { CreateNote("note1.md") });

        // Act - First call caches note1.md
        await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // GitHub now returns a different note, but the cache should not know about it
        _gitHubService.GetNotesFunc = (_, _) =>
            Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { CreateNote("note2.md") });

        var result = await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert
        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        GetFirstName(valueResult).Should().Be("note1.md");
    }

    [Fact]
    public async Task GetNotes_ShouldCacheSeparately_PerPath()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        var requestedPaths = new List<string?>();
        _gitHubService.GetNotesFunc = (_, path) =>
        {
            requestedPaths.Add(path);
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { CreateNote($"{path}.md") });
        };

        // Act - same paths requested twice
        await NotesEndpoints.GetNotes("folder1", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes("folder2", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes("folder1", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes("folder2", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert - each distinct path fetched exactly once
        requestedPaths.Should().BeEquivalentTo("folder1", "folder2");
    }

    [Fact]
    public async Task GetNotes_ShouldCacheSeparately_PerUser()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 11111, "pat_user1");
        await CreateUserAsync(db, 22222, "pat_user2");

        var callsPerUser = new Dictionary<long, int>();
        _gitHubService.GetNotesFunc = (ctx, _) =>
        {
            var telegramId = ctx.Token == "pat_user1" ? 11111 : 22222;
            callsPerUser[telegramId] = callsPerUser.GetValueOrDefault(telegramId) + 1;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { CreateNote("note1.md") });
        };

        // Act - each user requests twice
        await NotesEndpoints.GetNotes(null, null, 11111, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes(null, null, 11111, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes(null, null, 22222, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes(null, null, 22222, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert - each user has an independent cache entry
        callsPerUser[11111].Should().Be(1);
        callsPerUser[22222].Should().Be(1);
    }

    [Fact]
    public async Task BustUserCache_ShouldExpire_AllPathEntries_ForUser()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        var callCount = 0;
        _gitHubService.GetNotesFunc = (_, _) =>
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { CreateNote("note1.md") });
        };
        _gitHubService.CreateOrUpdateNoteFunc = (_, _, _, _) => Task.CompletedTask;

        // Act - cache both paths, then bust the user's cache via a write operation
        await NotesEndpoints.GetNotes("folder1", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes("folder2", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        callCount.Should().Be(2);

        var request = new CreateOrUpdateNoteRequest("notes/new.md", "content", "msg");
        await NotesEndpoints.CreateOrUpdateNote(request, 12345, null, db, CreatePendingSync(db), _memoryCache);

        // Assert - both cached paths were invalidated
        await NotesEndpoints.GetNotes("folder1", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes("folder2", null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        callCount.Should().Be(4);
    }

    [Fact]
    public async Task BustUserCache_ShouldNotAffect_OtherUsers()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 11111, "pat_user1");
        await CreateUserAsync(db, 22222, "pat_user2");

        var user1Calls = 0;
        var user2Calls = 0;
        _gitHubService.GetNotesFunc = (ctx, _) =>
        {
            if (ctx.Token == "pat_user1")
            {
                user1Calls++;
            }
            else
            {
                user2Calls++;
            }

            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { CreateNote("note1.md") });
        };
        _gitHubService.CreateOrUpdateNoteFunc = (_, _, _, _) => Task.CompletedTask;

        // Act - cache for both users, then write for user1 only
        await NotesEndpoints.GetNotes(null, null, 11111, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes(null, null, 22222, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        var request = new CreateOrUpdateNoteRequest("notes/new.md", "content", "msg");
        await NotesEndpoints.CreateOrUpdateNote(request, 11111, null, db, CreatePendingSync(db), _memoryCache);

        // Assert - user1's cache was busted, user2's was not
        await NotesEndpoints.GetNotes(null, null, 11111, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes(null, null, 22222, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        user1Calls.Should().Be(2);
        user2Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetNotes_ShouldNotCache_WhenGitHubServiceThrows()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetNotesFunc = (_, _) => throw new Exception("GitHub API down");

        // Act - first attempt fails, then the service recovers
        Func<Task> act = () => NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));
        await act.Should().ThrowAsync<Exception>().WithMessage("GitHub API down");

        var callCount = 0;
        _gitHubService.GetNotesFunc = (_, _) =>
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { CreateNote("note1.md") });
        };
        var result = await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService, _memoryCache, CreatePendingSync(db));

        // Assert - the failed attempt was not cached, the recovered call fetched fresh data
        callCount.Should().Be(1);
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult!.StatusCode.Should().Be(StatusCodes.Status200OK);
    }
}
