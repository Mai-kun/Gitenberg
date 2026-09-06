using System.Collections;
using System.Net;
using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Notes;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Models;
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

namespace Gitenberg.Tests.Features.Notes;

public class HistoryEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly MockGitHubService _gitHubService;
    private readonly IMemoryCache _memoryCache;

    public HistoryEndpointsTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
        _gitHubService = new MockGitHubService();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
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
        Gitenberg.Web.Features.Reminders.ReminderService.EnsureTableCreated(dbContext);
        return dbContext;
    }

    private static PendingSyncService CreatePendingSync(AppDbContext db)
    {
        PendingSyncService.EnsureTableCreated(db);
        return new PendingSyncService(db);
    }

    private IRepositoryContextResolver CreateResolver(AppDbContext db) =>
        new RepositoryContextResolver(db, _encryptionService, NullLogger<RepositoryContextResolver>.Instance);

    private static int ActiveRepoId(AppDbContext db, long telegramId) =>
        db.Repositories.Where(r => r.TelegramUserId == telegramId).OrderBy(r => r.Id).First().Id;

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

    private static List<NoteCommitInfo> GetCommits(object? value)
    {
        var commitsProp = value!.GetType().GetProperty("Commits");
        commitsProp.Should().NotBeNull();
        return ((IEnumerable<NoteCommitInfo>)commitsProp.GetValue(value)!).ToList();
    }

    [Fact]
    public async Task GetNoteHistory_ShouldReturnBadRequest_WhenTelegramIdIsNull()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await HistoryEndpoints.GetNoteHistory(
            "notes/idea.md", null, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNoteHistory_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await HistoryEndpoints.GetNoteHistory(
            null!, 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNoteHistory_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await HistoryEndpoints.GetNoteHistory(
            "notes/idea.md", 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetNoteHistory_ShouldReturnBadRequest_WhenGitHubTokenIsMissing()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345, token: null);

        var result = await HistoryEndpoints.GetNoteHistory(
            "notes/idea.md", 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNoteHistory_ShouldReturnOkWithCommits_WhenRequestIsValid()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        var commits = new List<NoteCommitInfo>
        {
            new("sha2", "Alice", "alice", "http://avatar", DateTimeOffset.Parse("2026-01-02T10:00:00Z"), "Update note: notes/idea.md"),
            new("sha1", "Bob", null, null, DateTimeOffset.Parse("2026-01-01T10:00:00Z"), "Create note: notes/idea.md"),
        };
        _gitHubService.GetCommitHistoryFunc = (ctx, path) =>
        {
            ctx.Token.Should().Be("pat_123");
            ctx.Owner.Should().Be("owner");
            ctx.Repo.Should().Be("repo");
            path.Should().Be("notes/idea.md");
            return Task.FromResult<IReadOnlyList<NoteCommitInfo>>(commits);
        };

        var result = await HistoryEndpoints.GetNoteHistory(
            "notes/idea.md", 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        var returned = GetCommits(valueResult.Value);
        returned.Should().HaveCount(2);
        returned[0].Sha.Should().Be("sha2");
        returned[0].AuthorName.Should().Be("Alice");
        returned[0].AuthorLogin.Should().Be("alice");
        returned[0].Message.Should().Be("Update note: notes/idea.md");

        var hasPendingProp = valueResult.Value!.GetType().GetProperty("HasPendingChanges");
        hasPendingProp.Should().NotBeNull();
        hasPendingProp.GetValue(valueResult.Value).Should().Be(false);
    }

    [Fact]
    public async Task GetNoteHistory_ShouldPassPathToService_AsIs()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        string? capturedPath = null;
        _gitHubService.GetCommitHistoryFunc = (_, path) =>
        {
            capturedPath = path;
            return Task.FromResult<IReadOnlyList<NoteCommitInfo>>(new List<NoteCommitInfo>());
        };

        await HistoryEndpoints.GetNoteHistory(
            "/notes/idea.md", 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        // Leading-slash normalization is GitHubService's job (it owns the
        // Octokit call); the endpoint forwards the raw path.
        capturedPath.Should().Be("/notes/idea.md");
    }

    [Fact]
    public async Task GetNoteHistory_ShouldReturnNotFound_WhenServiceThrowsNotFoundException()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetCommitHistoryFunc = (_, _) =>
            throw new NotFoundException("No commits", HttpStatusCode.NotFound);

        var result = await HistoryEndpoints.GetNoteHistory(
            "notes/missing.md", 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetNoteHistory_ShouldFlagPendingChanges_WhenNoteHasUnsyncedSave()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);
        var pendingSync = CreatePendingSync(db);
        await pendingSync.EnqueueAsync(12345, ActiveRepoId(db, 12345), "save", "notes/idea.md", null, "local draft");

        _gitHubService.GetCommitHistoryFunc = (_, _) =>
            Task.FromResult<IReadOnlyList<NoteCommitInfo>>(new List<NoteCommitInfo>
            {
                new("sha1", "Alice", null, null, DateTimeOffset.UtcNow, "Create note"),
            });

        var result = await HistoryEndpoints.GetNoteHistory(
            "notes/idea.md", 12345, null, db, CreateResolver(db), _gitHubService, pendingSync);

        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        var hasPendingProp = valueResult.Value!.GetType().GetProperty("HasPendingChanges");
        hasPendingProp.Should().NotBeNull();
        hasPendingProp.GetValue(valueResult.Value).Should().Be(true);
    }

    [Fact]
    public async Task GetNoteVersionContent_ShouldReturnBadRequest_WhenShaIsMissing()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await HistoryEndpoints.GetNoteVersionContent(
            "notes/idea.md", null!, 12345, null, db, CreateResolver(db), _gitHubService);

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNoteVersionContent_ShouldReturnOkWithContent_WhenRequestIsValid()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetNoteContentAtCommitFunc = (ctx, path, sha) =>
        {
            ctx.Token.Should().Be("pat_123");
            path.Should().Be("notes/idea.md");
            sha.Should().Be("abcdef1234567890");
            return Task.FromResult("# old version");
        };

        var result = await HistoryEndpoints.GetNoteVersionContent(
            "notes/idea.md", "abcdef1234567890", 12345, null, db, CreateResolver(db), _gitHubService);

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        var contentProp = valueResult.Value!.GetType().GetProperty("Content");
        contentProp.Should().NotBeNull();
        contentProp.GetValue(valueResult.Value).Should().Be("# old version");
    }

    [Fact]
    public async Task GetNoteVersionContent_ShouldReturnNotFound_WhenFileIsMissingAtCommit()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetNoteContentAtCommitFunc = (_, _, _) =>
            throw new NotFoundException("File not found at commit", HttpStatusCode.NotFound);

        var result = await HistoryEndpoints.GetNoteVersionContent(
            "notes/idea.md", "abcdef1234567890", 12345, null, db, CreateResolver(db), _gitHubService);

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task RestoreNoteVersion_ShouldReturnBadRequest_WhenShaIsMissing()
    {
        await using var db = CreateInMemoryDbContext();
        var request = new RestoreNoteVersionRequest("notes/idea.md", null!);

        var result = await HistoryEndpoints.RestoreNoteVersion(
            request, 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db), _memoryCache, new Gitenberg.Web.Features.Reminders.ReminderService(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task RestoreNoteVersion_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        await using var db = CreateInMemoryDbContext();
        var request = new RestoreNoteVersionRequest("notes/idea.md", "abcdef1234567890");

        var result = await HistoryEndpoints.RestoreNoteVersion(
            request, 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db), _memoryCache, new Gitenberg.Web.Features.Reminders.ReminderService(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task RestoreNoteVersion_ShouldReturnNotFound_WhenFileIsMissingAtCommit()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetNoteContentAtCommitFunc = (_, _, _) =>
            throw new NotFoundException("File not found at commit", HttpStatusCode.NotFound);

        var request = new RestoreNoteVersionRequest("notes/idea.md", "abcdef1234567890");
        var result = await HistoryEndpoints.RestoreNoteVersion(
            request, 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db), _memoryCache, new Gitenberg.Web.Features.Reminders.ReminderService(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        var pending = await CreatePendingSync(db).GetOpsAsync(12345, ActiveRepoId(db, 12345));
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreNoteVersion_ShouldEnqueueSaveOp_WithOldContentAndShortShaMessage()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetNoteContentAtCommitFunc = (_, path, sha) =>
        {
            // Raw path goes to the service; trimming happens inside GitHubService.
            path.Should().Be("/notes/idea.md");
            sha.Should().Be("abcdef1234567890");
            return Task.FromResult("# old version content");
        };

        var request = new RestoreNoteVersionRequest("/notes/idea.md", "abcdef1234567890");
        var result = await HistoryEndpoints.RestoreNoteVersion(
            request, 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db), _memoryCache, new Gitenberg.Web.Features.Reminders.ReminderService(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        // Local-first: the restore is queued as a regular save op with a
        // meaningful commit message (sha sliced to 7 chars, not formatted).
        var pending = await CreatePendingSync(db).GetOpsAsync(12345, ActiveRepoId(db, 12345));
        pending.Should().ContainSingle();
        pending[0].Kind.Should().Be("save");
        pending[0].FromPath.Should().Be("notes/idea.md");
        pending[0].Content.Should().Be("# old version content");
        pending[0].CommitMessage.Should().Be("Restore note: notes/idea.md (← abcdef1)");
    }

    [Fact]
    public async Task RestoreNoteVersion_ShouldBustNotesCache_WhenExecuted()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetNoteContentAtCommitFunc = (_, _, _) => Task.FromResult("old");
        var request = new RestoreNoteVersionRequest("notes/idea.md", "abcdef1234567890");

        // Prime the notes cache for two paths, restore, then both must refetch.
        await NotesEndpoints.GetNotes("folder1", null, 12345, db, CreateResolver(db), _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes("folder2", null, 12345, db, CreateResolver(db), _gitHubService, _memoryCache, CreatePendingSync(db));

        await HistoryEndpoints.RestoreNoteVersion(
            request, 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db), _memoryCache, new Gitenberg.Web.Features.Reminders.ReminderService(db));

        var requestedPaths = new List<string?>();
        _gitHubService.GetNotesFunc = (_, path) =>
        {
            requestedPaths.Add(path);
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>());
        };

        await NotesEndpoints.GetNotes("folder1", null, 12345, db, CreateResolver(db), _gitHubService, _memoryCache, CreatePendingSync(db));
        await NotesEndpoints.GetNotes("folder2", null, 12345, db, CreateResolver(db), _gitHubService, _memoryCache, CreatePendingSync(db));

        requestedPaths.Should().BeEquivalentTo("folder1", "folder2");
    }

    [Fact]
    public async Task GetNoteHistory_ShouldReturnEmptyList_WhenFileHasNoCommits()
    {
        await using var db = CreateInMemoryDbContext();
        await CreateUserAsync(db, 12345);

        _gitHubService.GetCommitHistoryFunc = (_, _) =>
            Task.FromResult<IReadOnlyList<NoteCommitInfo>>(new List<NoteCommitInfo>());

        var result = await HistoryEndpoints.GetNoteHistory(
            "notes/fresh.md", 12345, null, db, CreateResolver(db), _gitHubService, CreatePendingSync(db));

        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        GetCommits(valueResult.Value).Should().BeEmpty();
    }
}
