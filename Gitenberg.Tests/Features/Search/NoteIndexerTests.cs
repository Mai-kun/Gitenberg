using FluentAssertions;
using Gitenberg.Tests.Infrastructure.FakeClasses;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octokit;
using Xunit;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Search;

public class NoteIndexerTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly MockGitHubService _gitHubService;
    private readonly FakeLogger<NoteIndexer> _logger;

    public NoteIndexerTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
        _gitHubService = new MockGitHubService();
        _logger = new FakeLogger<NoteIndexer>();
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
            GitHubToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10)),
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static Task<List<string>> GetIndexedPathsAsync(AppDbContext db, string term)
    {
        return db.Database.SqlQuery<string>(
            $"SELECT NotePath AS Value FROM NoteSearchFts WHERE NoteSearchFts MATCH {term}"
        ).ToListAsync();
    }

    private NoteIndexer CreateIndexer(AppDbContext db)
    {
        return new NoteIndexer(db, _gitHubService, _encryptionService, _logger);
    }

    [Fact]
    public async Task SynchronizeAllUsers_ShouldIndexNewMarkdownNotes()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db);

        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(
            [
                CreateRepositoryContent("note1.md", "notes/note1.md", "sha1"),
                CreateRepositoryContent("image.png", "notes/image.png", "sha2"),
            ]
        );
        _gitHubService.GetNoteContentFunc = (_, path) =>
            Task.FromResult($"Content of {path} about kernel scheduling.");

        // Act
        await CreateIndexer(db).SynchronizeAllUsersAsync();

        // Assert
        var indexedNote = await db.IndexedNotes.SingleAsync();
        indexedNote.NotePath.Should().Be("notes/note1.md");
        indexedNote.Sha.Should().Be("sha1");
        indexedNote.TelegramUserId.Should().Be(12345);

        (await GetIndexedPathsAsync(db, "kernel")).Should().Contain("notes/note1.md");
    }

    [Fact]
    public async Task SynchronizeAllUsers_ShouldNotDownloadContent_WhenShaIsUnchanged()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db);

        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(
            [CreateRepositoryContent("note1.md", "notes/note1.md", "sha1")]
        );

        var downloadCount = 0;
        _gitHubService.GetNoteContentFunc = (_, _) =>
        {
            downloadCount++;
            return Task.FromResult("Content about kernel scheduling.");
        };

        var indexer = CreateIndexer(db);

        // Act
        await indexer.SynchronizeAllUsersAsync();
        await indexer.SynchronizeAllUsersAsync();

        // Assert
        downloadCount.Should().Be(1, "unchanged SHA must not trigger a second content download");
        db.IndexedNotes.Should().ContainSingle();
    }

    [Fact]
    public async Task SynchronizeAllUsers_ShouldReindexNote_WhenShaChanges()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db);

        var sha = "sha1";
        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(
            [CreateRepositoryContent("note1.md", "notes/note1.md", sha)]
        );
        _gitHubService.GetNoteContentFunc = (_, _) => Task.FromResult("The alpha release notes.");

        var indexer = CreateIndexer(db);

        // Act
        await indexer.SynchronizeAllUsersAsync();

        sha = "sha2";
        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(
            [CreateRepositoryContent("note1.md", "notes/note1.md", sha)]
        );
        _gitHubService.GetNoteContentFunc = (_, _) => Task.FromResult("The beta release notes.");

        await indexer.SynchronizeAllUsersAsync();

        // Assert
        var indexedNote = await db.IndexedNotes.SingleAsync();
        indexedNote.Sha.Should().Be("sha2");

        (await GetIndexedPathsAsync(db, "beta")).Should().Contain("notes/note1.md");
        (await GetIndexedPathsAsync(db, "alpha")).Should().BeEmpty("old content must be replaced in the FTS index");
    }

    [Fact]
    public async Task SynchronizeAllUsers_ShouldRemoveNotesDeletedOnGitHub()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db);

        var remotePaths = new List<string> { "notes/keep.md", "notes/delete.md" };

        static RepositoryContent NoteFor(string path) =>
            CreateRepositoryContent(Path.GetFileName(path), path, $"sha-{path}");

        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(
            remotePaths.Select(NoteFor).ToList()
        );
        _gitHubService.GetNoteContentFunc = (_, path) => Task.FromResult($"Content of {path} about kernel scheduling.");

        var indexer = CreateIndexer(db);
        await indexer.SynchronizeAllUsersAsync();

        // Act - the second note is deleted on GitHub.
        remotePaths.Remove("notes/delete.md");
        await indexer.SynchronizeAllUsersAsync();

        // Assert
        var indexedNote = await db.IndexedNotes.SingleAsync();
        indexedNote.NotePath.Should().Be("notes/keep.md");

        (await GetIndexedPathsAsync(db, "kernel")).Should().ContainSingle().Which.Should().Be("notes/keep.md");
    }

    [Fact]
    public async Task SynchronizeAllUsers_ShouldSkipUserWithoutGitHubToken()
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

        var fetchCount = 0;
        _gitHubService.GetNotesFunc = (_, _) =>
        {
            fetchCount++;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>([]);
        };

        // Act
        await CreateIndexer(db).SynchronizeAllUsersAsync();

        // Assert
        fetchCount.Should().Be(0);
        db.IndexedNotes.Should().BeEmpty();
    }
}
