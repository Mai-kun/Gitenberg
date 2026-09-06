using System.Net;
using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Repositories;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Repository = Gitenberg.Web.Models.Repository;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Repositories;

public class RepositoryEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;

    public RepositoryEndpointsTests()
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
        PendingSyncService.EnsureTableCreated(dbContext);
        Gitenberg.Web.Features.Reminders.ReminderService.EnsureTableCreated(dbContext);
        dbContext.EnsureFtsTableCreated();
        return dbContext;
    }

    private async Task<(User User, Repository Repository)> SeedUserWithRepositoryAsync(
        AppDbContext db,
        long telegramId = 12345,
        string owner = "owner",
        string repo = "first",
        string? token = "token_1",
        string inboxPath = "inbox",
        string attachmentsPath = "inbox/attachments"
    )
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
            DisplayName = $"{owner}/{repo}",
            RepositoryOwner = owner,
            RepositoryName = repo,
            GitHubToken = token == null ? null : _encryptionService.EncryptToken(token, TimeSpan.FromMinutes(10)),
            InboxPath = inboxPath,
            AttachmentsPath = attachmentsPath,
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();
        return (user, repository);
    }

    // -------------------------------------------------------------------
    // GET /api/repositories
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListRepositories_ShouldReturnBadRequest_WhenTelegramIdIsMissing()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await RepositoriesEndpoints.ListRepositories(null, null, db);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ListRepositories_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        await using var db = CreateInMemoryDbContext();

        var result = await RepositoriesEndpoints.ListRepositories(null, 12345, db);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ListRepositories_ShouldReturnReposWithHasTokenAndActiveFlag_NeverTheToken()
    {
        await using var db = CreateInMemoryDbContext();
        var (user, first) = await SeedUserWithRepositoryAsync(db);
        db.Repositories.Add(new Repository
        {
            TelegramUserId = user.TelegramId,
            DisplayName = "Работа",
            RepositoryOwner = "work-owner",
            RepositoryName = "work-repo",
            GitHubToken = null,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await RepositoriesEndpoints.ListRepositories(null, 12345, db);

        var value = (result as IValueHttpResult)!.Value.Should().BeAssignableTo<List<object>>().Subject;
        value.Should().HaveCount(2);

        // Token values are never returned, only a has-token flag.
        object GetProp(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)!;
        GetProp(value[0], "HasToken").Should().Be(true);
        GetProp(value[1], "HasToken").Should().Be(false);
        GetProp(value[0], "IsActive").Should().Be(true);
        GetProp(value[1], "IsActive").Should().Be(false);
        System.Text.Json.JsonSerializer.Serialize(value).Should().NotContain("token_1");
    }

    // -------------------------------------------------------------------
    // POST /api/repositories
    // -------------------------------------------------------------------

    [Fact]
    public async Task CreateRepository_ShouldRequireToken()
    {
        await using var db = CreateInMemoryDbContext();
        await SeedUserWithRepositoryAsync(db);
        var request = new CreateRepositoryRequest("Работа", "", "owner", "second");

        var result = await RepositoriesEndpoints.CreateRepository(request, null, 12345, db, _encryptionService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task CreateRepository_ShouldEncryptToken_AndKeepActiveSelection()
    {
        await using var db = CreateInMemoryDbContext();
        var (user, first) = await SeedUserWithRepositoryAsync(db);
        var request = new CreateRepositoryRequest("Личное", "pat_second", "personal", "notes", InboxPath: "/my-inbox/");

        var result = await RepositoriesEndpoints.CreateRepository(request, null, 12345, db, _encryptionService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status200OK);

        var created = await db.Repositories.SingleAsync(r => r.RepositoryName == "notes");
        created.DisplayName.Should().Be("Личное");
        created.RepositoryOwner.Should().Be("personal");
        created.InboxPath.Should().Be("my-inbox");
        _encryptionService.DecryptToken(created.GitHubToken!).Should().Be("pat_second");

        // The previously active repository stays active.
        db.Entry(user).Reload();
        user.SelectedRepositoryId.Should().Be(first.Id);
    }

    [Fact]
    public async Task CreateRepository_ShouldActivateFirstRepository_WhenUserHadNone()
    {
        await using var db = CreateInMemoryDbContext();
        var user = new User { TelegramId = 12345, CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var request = new CreateRepositoryRequest("", "pat_1", "owner", "first");
        var result = await RepositoriesEndpoints.CreateRepository(request, null, 12345, db, _encryptionService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status200OK);
        db.Entry(user).Reload();
        user.SelectedRepositoryId.Should().NotBeNull();
        var created = await db.Repositories.SingleAsync(r => r.TelegramUserId == 12345);
        created.DisplayName.Should().Be("owner/first");
    }

    [Fact]
    public async Task CreateRepository_ShouldRejectUserOverTheLimit()
    {
        await using var db = CreateInMemoryDbContext();
        var (user, _) = await SeedUserWithRepositoryAsync(db);
        for (var i = 2; i <= RepositoriesEndpoints.MaxRepositoriesPerUser; i++)
        {
            db.Repositories.Add(new Repository
            {
                TelegramUserId = user.TelegramId,
                DisplayName = $"repo-{i}",
                RepositoryOwner = "owner",
                RepositoryName = $"repo-{i}",
                GitHubToken = "t",
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var request = new CreateRepositoryRequest("over", "pat_x", "owner", "over");
        var result = await RepositoriesEndpoints.CreateRepository(request, null, 12345, db, _encryptionService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // -------------------------------------------------------------------
    // PUT /api/repositories/{id}
    // -------------------------------------------------------------------

    [Fact]
    public async Task UpdateRepository_ShouldKeepStoredToken_WhenTokenOmitted()
    {
        await using var db = CreateInMemoryDbContext();
        var (_, repository) = await SeedUserWithRepositoryAsync(db);
        var request = new UpdateRepositoryRequest("Личное", null, "new-owner", "renamed");

        var result = await RepositoriesEndpoints.UpdateRepository(repository.Id, request, null, 12345, db, _encryptionService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status200OK);
        db.Entry(repository).Reload();
        repository.DisplayName.Should().Be("Личное");
        repository.RepositoryOwner.Should().Be("new-owner");
        repository.RepositoryName.Should().Be("renamed");
        _encryptionService.DecryptToken(repository.GitHubToken!).Should().Be("token_1");
    }

    [Fact]
    public async Task UpdateRepository_ShouldReturnNotFound_ForAnotherUsersRepository()
    {
        await using var db = CreateInMemoryDbContext();
        var (_, repository) = await SeedUserWithRepositoryAsync(db);
        await SeedUserWithRepositoryAsync(db, telegramId: 99999, owner: "o", repo: "r2");
        var request = new UpdateRepositoryRequest(null, null, "owner", "first");

        var result = await RepositoriesEndpoints.UpdateRepository(repository.Id, request, null, 99999, db, _encryptionService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // -------------------------------------------------------------------
    // POST /api/repositories/{id}/activate
    // -------------------------------------------------------------------

    [Fact]
    public async Task ActivateRepository_ShouldSwitchActiveRepository()
    {
        await using var db = CreateInMemoryDbContext();
        var (user, first) = await SeedUserWithRepositoryAsync(db);
        var second = new Repository
        {
            TelegramUserId = user.TelegramId,
            DisplayName = "Работа",
            RepositoryOwner = "work",
            RepositoryName = "notes",
            GitHubToken = "t",
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(second);
        await db.SaveChangesAsync();

        var result = await RepositoriesEndpoints.ActivateRepository(second.Id, null, 12345, db);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status200OK);
        db.Entry(user).Reload();
        user.SelectedRepositoryId.Should().Be(second.Id);
        user.SelectedRepositoryId.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task ActivateRepository_ShouldReturnNotFound_ForAnotherUsersRepository()
    {
        await using var db = CreateInMemoryDbContext();
        var (_, first) = await SeedUserWithRepositoryAsync(db);
        var (otherUser, otherRepo) = await SeedUserWithRepositoryAsync(db, telegramId: 99999, owner: "o", repo: "r2");

        var result = await RepositoriesEndpoints.ActivateRepository(first.Id, null, 99999, db);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        db.Entry(otherUser).Reload();
        otherUser.SelectedRepositoryId.Should().Be(otherRepo.Id);
    }

    // -------------------------------------------------------------------
    // DELETE /api/repositories/{id}
    // -------------------------------------------------------------------

    [Fact]
    public async Task DeleteRepository_ShouldRejectDeletingTheLastOne()
    {
        await using var db = CreateInMemoryDbContext();
        var (_, repository) = await SeedUserWithRepositoryAsync(db);

        var result = await RepositoriesEndpoints.DeleteRepository(repository.Id, null, 12345, db);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        db.Repositories.Should().Contain(r => r.Id == repository.Id);
    }

    [Fact]
    public async Task DeleteRepository_ShouldCascadeCleanRepoScopedData_AndReactivateAnother()
    {
        await using var db = CreateInMemoryDbContext();
        var (user, first) = await SeedUserWithRepositoryAsync(db);
        var second = new Repository
        {
            TelegramUserId = user.TelegramId,
            DisplayName = "Работа",
            RepositoryOwner = "work",
            RepositoryName = "notes",
            GitHubToken = "t",
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(second);
        await db.SaveChangesAsync();

        // Repo-scoped data attached to the first (active) repository.
        db.IndexedNotes.Add(new IndexedNote { TelegramUserId = user.TelegramId, RepositoryId = first.Id, NotePath = "inbox/a.md", Sha = "s1" });
        db.IndexedNotes.Add(new IndexedNote { TelegramUserId = user.TelegramId, RepositoryId = second.Id, NotePath = "inbox/a.md", Sha = "s2" });
        await db.SaveChangesAsync();
        db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO NoteSearchFts (TelegramUserId, RepositoryId, NotePath, Content) VALUES ({user.TelegramId.ToString()}, {first.Id.ToString()}, 'inbox/a.md', 'inbox/a.md\ncontent')"
        ).GetAwaiter().GetResult();
        var pendingSync = new PendingSyncService(db);
        await pendingSync.EnqueueAsync(user.TelegramId, first.Id, "save", "inbox/a.md", null, "local draft");
        await new Gitenberg.Web.Features.Reminders.ReminderService(db)
            .UpsertForNoteAsync(user.TelegramId, first.Id, "inbox/a.md", "@remind 1h");


        var result = await RepositoriesEndpoints.DeleteRepository(first.Id, null, 12345, db);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status200OK);

        // The repository and its scoped rows are gone...
        db.Repositories.Should().NotContain(r => r.Id == first.Id);
        db.IndexedNotes.Should().NotContain(n => n.RepositoryId == first.Id);
        (await db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM NoteSearchFts WHERE RepositoryId = {first.Id.ToString()}"
        ).SingleAsync()).Should().Be(0);
        (await pendingSync.GetOpsAsync(user.TelegramId, first.Id)).Should().BeEmpty();
        (await db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS Value FROM Reminders WHERE RepositoryId = {first.Id.ToString()}"
        ).SingleAsync()).Should().Be(0);

        // ...the other repository's data survives and becomes active.
        db.IndexedNotes.Should().Contain(n => n.RepositoryId == second.Id);
        db.Entry(user).Reload();
        user.SelectedRepositoryId.Should().Be(second.Id);
    }

    [Fact]
    public async Task DeleteRepository_ShouldReturnNotFound_ForAnotherUsersRepository()
    {
        await using var db = CreateInMemoryDbContext();
        var (_, repository) = await SeedUserWithRepositoryAsync(db);

        var result = await RepositoriesEndpoints.DeleteRepository(repository.Id, null, 55555, db);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        db.Repositories.Should().Contain(r => r.Id == repository.Id);
    }
}
