using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.Models;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Services;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gitenberg.Tests.Features.Sync;

/// <summary>
/// Pending ops and reminders are scoped per repository: identical note paths
/// in two repositories ("Personal" and "Work") must stay fully independent.
/// </summary>
public class RepositoryScopedDataTests
{
    private readonly TokenEncryptionService _encryptionService;

    public RepositoryScopedDataTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
    }

    private AppDbContext CreateDbContext()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;

        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();
        PendingSyncService.EnsureTableCreated(dbContext);
        ReminderService.EnsureTableCreated(dbContext);
        dbContext.EnsureFtsTableCreated();
        return dbContext;
    }

    private IRepositoryContextResolver CreateResolver(AppDbContext db) =>
        new RepositoryContextResolver(db, _encryptionService, NullLogger<RepositoryContextResolver>.Instance);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public async Task PendingOps_StayIsolated_PerRepository(int firstRepoId, int secondRepoId)
    {
        await using var db = CreateDbContext();
        var pendingSync = new PendingSyncService(db);

        // The same note path saved into two different repositories.
        await pendingSync.EnqueueAsync(42, firstRepoId, "save", "inbox/today.md", null, "personal text");
        await pendingSync.EnqueueAsync(42, secondRepoId, "save", "inbox/today.md", null, "work text");

        var firstOps = await pendingSync.GetOpsAsync(42, firstRepoId);
        var secondOps = await pendingSync.GetOpsAsync(42, secondRepoId);

        firstOps.Should().ContainSingle();
        firstOps[0].Content.Should().Be("personal text");
        secondOps.Should().ContainSingle();
        secondOps[0].Content.Should().Be("work text");

        (await pendingSync.GetPendingCountAsync(42, firstRepoId)).Should().Be(1);
        (await pendingSync.GetPendingCountAsync(42, secondRepoId)).Should().Be(1);

        // Flushing one repository leaves the other queue untouched.
        var gitHubService = new MockGitHubService();
        var context = new Gitenberg.Web.Models.GitHubRepositoryContext("t", "owner", "repo");
        var (applied, remaining) = await pendingSync.FlushUserAsync(42, firstRepoId, context, gitHubService);
        applied.Should().Be(1);
        remaining.Should().Be(0);

        (await pendingSync.GetOpsAsync(42, firstRepoId)).Should().BeEmpty();
        (await pendingSync.GetOpsAsync(42, secondRepoId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Reminders_StayIsolated_PerRepository()
    {
        await using var db = CreateDbContext();
        var reminderService = new ReminderService(db);

        // Identical note path and marker in two repositories.
        const string content = "# День\n\n@remind 2h Встать";
        await reminderService.UpsertForNoteAsync(42, 1, "inbox/today.md", content);
        await reminderService.UpsertForNoteAsync(42, 2, "inbox/today.md", content);

        // Both reminders exist, one per repository.
        var due = await reminderService.GetDueAsync(DateTime.UtcNow.AddHours(3));
        due.Should().HaveCount(2);
        due.Select(r => r.RepositoryId).Should().BeEquivalentTo([1, 2]);

        // Erasing the marker in repository 1 keeps repository 2's reminder.
        await reminderService.UpsertForNoteAsync(42, 1, "inbox/today.md", "# День без напоминания");
        due = await reminderService.GetDueAsync(DateTime.UtcNow.AddHours(3));
        due.Should().ContainSingle();
        due[0].RepositoryId.Should().Be(2);

        // Removing the note in repository 2 clears only its own reminder.
        await reminderService.RemoveForNoteAsync(42, 2, "inbox/today.md");
        (await reminderService.GetDueAsync(DateTime.UtcNow.AddHours(3))).Should().BeEmpty();
    }

    [Fact]
    public async Task Resolver_ResolvesSpecificRepository_AndReturnsNull_WhenNothingToResolve()
    {
        await using var db = CreateDbContext();
        var user = new Gitenberg.Web.Models.User { TelegramId = 42, CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var repository = new Gitenberg.Web.Models.Repository
        {
            TelegramUserId = 42,
            DisplayName = "Работа",
            RepositoryOwner = "work-owner",
            RepositoryName = "work-notes",
            GitHubToken = _encryptionService.EncryptToken("pat_work", TimeSpan.FromMinutes(10)),
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();

        var resolver = CreateResolver(db);

        // Explicit id resolution works for the owner...
        var byId = await resolver.ResolveByIdAsync(42, repository.Id);
        byId.Should().NotBeNull();
        byId!.Context.Token.Should().Be("pat_work");
        byId.Context.Repo.Should().Be("work-notes");

        // ...and fails for anyone else or for a foreign id.
        (await resolver.ResolveByIdAsync(99999, repository.Id)).Should().BeNull();
        (await resolver.ResolveByIdAsync(42, repository.Id + 777)).Should().BeNull();

        // A user without repositories resolves to nothing.
        db.Users.Add(new Gitenberg.Web.Models.User { TelegramId = 777, CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        (await resolver.ResolveActiveAsync(777)).Should().BeNull();
    }
}
