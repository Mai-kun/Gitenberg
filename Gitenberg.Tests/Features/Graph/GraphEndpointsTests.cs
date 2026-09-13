using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Graph;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Gitenberg.Tests.Mocks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Gitenberg.Web.Features.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Graph;

public class GraphEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;

    public GraphEndpointsTests()
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
        PendingSyncService.EnsureTableCreated(dbContext);
        return dbContext;
    }

    private async Task SeedUserAsync(AppDbContext db, long userId)
    {
        var user = new User
        {
            TelegramId = userId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var repository = new Gitenberg.Web.Models.Repository
        {
            TelegramUserId = userId,
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
    }

    // The indexer stores "path\ncontent" in the FTS Content column, so the
    // seeded rows follow that format.
    private static Task InsertFtsRowAsync(AppDbContext db, long userId, string notePath, string body)
    {
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO NoteSearchFts (TelegramUserId, RepositoryId, NotePath, Content) VALUES ({userId.ToString()}, '1', {notePath}, {notePath + "\n" + body})"
        );
    }

    private IRepositoryContextResolver CreateResolver(AppDbContext db) =>
        new RepositoryContextResolver(db, _encryptionService, NullLogger<RepositoryContextResolver>.Instance);

    private NoteIndexer CreateIndexer(AppDbContext db)
    {
        return new NoteIndexer(db, new MockGitHubService(), _encryptionService, NullLogger<NoteIndexer>.Instance);
    }

    private static PendingSyncService CreatePendingSync(AppDbContext db) => new(db);

    private static HttpContext AuthenticatedContext(long userId)
    {
        var context = new DefaultHttpContext();
        context.Items[CurrentUserId.ItemsKey] = userId;
        return context;
    }

    private static WikiGraph GetGraphValue(IResult result)
    {
        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        return valueResult!.Value.Should().BeAssignableTo<WikiGraph>().Subject;
    }

    [Fact]
    public async Task GetGraph_ShouldReturnBadRequest_WhenUserIsNotAuthenticated()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await GraphEndpoints.GetGraph(db, CreateResolver(db), CreateIndexer(db), CreatePendingSync(db));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetGraph_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await GraphEndpoints.GetGraph(db, CreateResolver(db), CreateIndexer(db), CreatePendingSync(db), AuthenticatedContext(12345));

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetGraph_ShouldReturnEmptyGraph_WhenNoRepositoryIsConfigured()
    {
        await using var db = CreateInMemoryDbContext();
        var user = new User { TelegramId = 12345, CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await GraphEndpoints.GetGraph(db, CreateResolver(db), CreateIndexer(db), CreatePendingSync(db), AuthenticatedContext(12345));

        var graph = GetGraphValue(result);
        graph.Nodes.Should().BeEmpty();
        graph.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGraph_ShouldBuildNodesAndLinks_FromIndexedNotes()
    {
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await InsertFtsRowAsync(db, 12345, "inbox/home.md", "Start here, continue to [[ideas]].");
        await InsertFtsRowAsync(db, 12345, "projects/ideas.md", "An idea linking back to [[home]].");
        await InsertFtsRowAsync(db, 12345, "misc/lonely.md", "Nobody links to me.");

        var result = await GraphEndpoints.GetGraph(db, CreateResolver(db), CreateIndexer(db), CreatePendingSync(db), AuthenticatedContext(12345));

        var graph = GetGraphValue(result);
        graph.Nodes.Should().HaveCount(3);
        graph.Nodes.Should().Contain(n => n.Path == "inbox/home.md" && n.Name == "home" && n.Degree == 2);
        graph.Nodes.Should().Contain(n => n.Path == "misc/lonely.md" && n.Degree == 0);
        graph.Links.Should().Contain(l => l.Source == "inbox/home.md" && l.Target == "projects/ideas.md");
        graph.Links.Should().Contain(l => l.Source == "projects/ideas.md" && l.Target == "inbox/home.md");
    }

    [Fact]
    public async Task GetGraph_ShouldOnlyUseNotesOfTheAuthenticatedUser()
    {
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await SeedUserAsync(db, 67890);
        await InsertFtsRowAsync(db, 12345, "mine.md", "[[target]]");
        await InsertFtsRowAsync(db, 67890, "other-user.md", "[[mine]]");

        var result = await GraphEndpoints.GetGraph(db, CreateResolver(db), CreateIndexer(db), CreatePendingSync(db), AuthenticatedContext(12345));

        var graph = GetGraphValue(result);
        graph.Nodes.Should().ContainSingle().Which.Path.Should().Be("mine.md");
        graph.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGraph_ShouldApplyPendingSaveOverIndexedContent()
    {
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await InsertFtsRowAsync(db, 12345, "a.md", "Old content without links.");
        await InsertFtsRowAsync(db, 12345, "b.md", "Target.");

        var pendingSync = CreatePendingSync(db);
        await pendingSync.EnqueueAsync(12345, 1, "save", "a.md", null, "Now links to [[b]].");

        var result = await GraphEndpoints.GetGraph(db, CreateResolver(db), CreateIndexer(db), CreatePendingSync(db), AuthenticatedContext(12345));

        var graph = GetGraphValue(result);
        graph.Links.Should().ContainSingle(l => l.Source == "a.md" && l.Target == "b.md");
    }

    [Fact]
    public async Task GetGraph_ShouldIncludeBrandNewPendingNotes_AndExcludeDeletedOnes()
    {
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await InsertFtsRowAsync(db, 12345, "existing.md", "[[draft]] [[doomed]]");

        var pendingSync = CreatePendingSync(db);
        await pendingSync.EnqueueAsync(12345, 1, "delete", "doomed.md");
        await pendingSync.EnqueueAsync(12345, 1, "save", "draft.md", null, "Pending draft.");

        var result = await GraphEndpoints.GetGraph(db, CreateResolver(db), CreateIndexer(db), CreatePendingSync(db), AuthenticatedContext(12345));

        var graph = GetGraphValue(result);
        graph.Nodes.Select(n => n.Path).Should().BeEquivalentTo("existing.md", "draft.md");
        graph.Links.Should().ContainSingle(l => l.Source == "existing.md" && l.Target == "draft.md");
    }

    [Fact]
    public async Task GetGraph_ShouldFollowPendingMove()
    {
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await InsertFtsRowAsync(db, 12345, "old.md", "Content linking to [[b]].");
        await InsertFtsRowAsync(db, 12345, "b.md", "Target.");

        var pendingSync = CreatePendingSync(db);
        await pendingSync.EnqueueAsync(12345, 1, "move", "old.md", "new/place.md", "Content linking to [[b]].");

        var result = await GraphEndpoints.GetGraph(db, CreateResolver(db), CreateIndexer(db), CreatePendingSync(db), AuthenticatedContext(12345));

        var graph = GetGraphValue(result);
        graph.Nodes.Select(n => n.Path).Should().BeEquivalentTo("new/place.md", "b.md");
        graph.Links.Should().ContainSingle(l => l.Source == "new/place.md" && l.Target == "b.md");
    }
}
