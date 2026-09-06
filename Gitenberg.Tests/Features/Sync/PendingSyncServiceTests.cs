using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gitenberg.Tests.Features.Sync;

public class PendingSyncServiceTests
{
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

    private static PendingSyncService CreatePendingSync(AppDbContext db)
    {
        PendingSyncService.EnsureTableCreated(db);
        return new PendingSyncService(db);
    }

    private static GitHubRepositoryContext CreateContext() => new("token", "owner", "repo");

    [Fact]
    public async Task FlushUserAsync_ShouldUseOpCommitMessage_WhenPresent()
    {
        await using var db = CreateInMemoryDbContext();
        var pendingSync = CreatePendingSync(db);
        await pendingSync.EnqueueAsync(1, "save", "notes/idea.md", null, "old content", "Restore note: notes/idea.md (← abcdef1)");

        var gitHubService = new MockGitHubService();
        var (applied, remaining) = await pendingSync.FlushUserAsync(1, CreateContext(), gitHubService);

        applied.Should().Be(1);
        remaining.Should().Be(0);
        gitHubService.SavedNotes.Should().ContainSingle();
        gitHubService.SavedNotes[0].Path.Should().Be("notes/idea.md");
        gitHubService.SavedNotes[0].Content.Should().Be("old content");
        gitHubService.SavedNotes[0].CommitMessage.Should().Be("Restore note: notes/idea.md (← abcdef1)");
    }

    [Fact]
    public async Task FlushUserAsync_ShouldUseDefaultMessage_WhenCommitMessageIsMissing()
    {
        await using var db = CreateInMemoryDbContext();
        var pendingSync = CreatePendingSync(db);
        await pendingSync.EnqueueAsync(1, "save", "notes/idea.md", null, "new content");

        var gitHubService = new MockGitHubService();
        await pendingSync.FlushUserAsync(1, CreateContext(), gitHubService);

        gitHubService.SavedNotes.Should().ContainSingle();
        gitHubService.SavedNotes[0].CommitMessage.Should().Be("Update note: notes/idea.md");
    }

    [Fact]
    public async Task EnsureTableCreated_ShouldAddCommitMessageColumn_ToLegacyDatabases()
    {
        await using var db = CreateInMemoryDbContext();

        // Simulate a database created before CommitMessage existed.
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE PendingNoteOps (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TelegramUserId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                FromPath TEXT NOT NULL,
                ToPath TEXT NULL,
                Content TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """);

        PendingSyncService.EnsureTableCreated(db);

        // The migration makes new writes with a commit message work.
        var pendingSync = new PendingSyncService(db);
        await pendingSync.EnqueueAsync(1, "save", "notes/idea.md", null, "content", "Restore note: notes/idea.md (← abcdef1)");
        var ops = await pendingSync.GetOpsAsync(1);

        ops.Should().ContainSingle();
        ops[0].CommitMessage.Should().Be("Restore note: notes/idea.md (← abcdef1)");
    }
}
