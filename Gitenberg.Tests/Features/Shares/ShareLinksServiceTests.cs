using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Pins;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Features.Repositories;
using Gitenberg.Web.Features.Shares;
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

namespace Gitenberg.Tests.Features.Shares;

public class ShareLinksServiceTests
{
    private readonly TokenEncryptionService _encryptionService;

    public ShareLinksServiceTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
    }

    private AppDbContext CreateDb()
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
        PinsService.EnsureTableCreated(dbContext);
        ShareLinksService.EnsureTableCreated(dbContext);
        dbContext.EnsureFtsTableCreated();
        return dbContext;
    }

    private async Task<(User User, Repository Repository)> SeedUserWithRepositoryAsync(
        AppDbContext db,
        long telegramId = 12345,
        string owner = "owner",
        string repo = "first",
        string token = "token_1"
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
            GitHubToken = _encryptionService.EncryptToken(token, TimeSpan.FromMinutes(10)),
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return (user, repository);
    }

    // -------------------------------------------------------------------
    // CreateOrGetAsync
    // -------------------------------------------------------------------

    [Fact]
    public async Task CreateOrGetAsync_PersistsLink_AndReturnsIt()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);

        var link = await service.CreateOrGetAsync(12345, 1, "inbox/today.md");

        link.Token.Should().NotBeNullOrWhiteSpace();
        link.NotePath.Should().Be("inbox/today.md");
        link.TelegramUserId.Should().Be(12345);
        link.RepositoryId.Should().Be(1);
        link.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);

        var reread = await service.GetByTokenAsync(link.Token);
        reread.Should().NotBeNull();
        reread!.NotePath.Should().Be("inbox/today.md");
    }

    [Fact]
    public async Task CreateOrGetAsync_IsIdempotent()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);

        var first = await service.CreateOrGetAsync(12345, 1, "inbox/today.md");
        var second = await service.CreateOrGetAsync(12345, 1, "inbox/today.md");

        second.Token.Should().Be(first.Token, "the same note must keep its URL across calls");
    }

    [Fact]
    public async Task CreateOrGetAsync_NormalizesSlashes()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);

        var leading = await service.CreateOrGetAsync(12345, 1, "/inbox/today.md/");
        var plain = await service.CreateOrGetAsync(12345, 1, "inbox/today.md");

        leading.Token.Should().Be(plain.Token, "/notes/idea.md and notes/idea.md are the same note");
        leading.NotePath.Should().Be("inbox/today.md");
    }

    [Fact]
    public async Task CreateOrGetAsync_DistinctTokensPerNote()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);

        var a = await service.CreateOrGetAsync(12345, 1, "notes/a.md");
        var b = await service.CreateOrGetAsync(12345, 1, "notes/b.md");

        a.Token.Should().NotBe(b.Token);
        // 32 hex chars = 128 bits of entropy: the token is the only secret.
        a.Token.Should().HaveLength(32).And.MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public async Task CreateOrGetAsync_ScopesPerUserAndRepository()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);

        var first = await service.CreateOrGetAsync(1, 1, "notes/a.md");
        var second = await service.CreateOrGetAsync(2, 1, "notes/a.md");
        var otherRepo = await service.CreateOrGetAsync(1, 2, "notes/a.md");

        first.Token.Should().NotBe(second.Token);
        first.Token.Should().NotBe(otherRepo.Token);
    }

    // -------------------------------------------------------------------
    // RevokeAsync / GetByTokenAsync
    // -------------------------------------------------------------------

    [Fact]
    public async Task RevokeAsync_MakesTheTokenInactive()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);
        var link = await service.CreateOrGetAsync(12345, 1, "notes/a.md");

        var revoked = await service.RevokeAsync(12345, link.Token);

        revoked.Should().BeTrue();
        (await service.GetByTokenAsync(link.Token)).Should().BeNull("a revoked URL must stop working");
        (await service.GetActiveAsync(12345, 1, "notes/a.md")).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_IsOwnerScoped()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);
        var link = await service.CreateOrGetAsync(12345, 1, "notes/a.md");

        var revoked = await service.RevokeAsync(999, link.Token);

        revoked.Should().BeFalse("another user must not revoke someone else's link");
        (await service.GetByTokenAsync(link.Token)).Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeAsync_ToleratesUnknownToken()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);

        var revoked = await service.RevokeAsync(12345, "does-not-exist");

        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task CreateOrGetAsync_AfterRevoke_GeneratesAFreshToken()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);
        var original = await service.CreateOrGetAsync(12345, 1, "notes/a.md");
        await service.RevokeAsync(12345, original.Token);

        var recreated = await service.CreateOrGetAsync(12345, 1, "notes/a.md");

        recreated.Token.Should().NotBe(original.Token);
    }

    // -------------------------------------------------------------------
    // ReassignOnMoveAsync / RemoveForPathAsync
    // -------------------------------------------------------------------

    [Fact]
    public async Task ReassignOnMove_RewritesExactAndChildPaths_OnlyForTheRepository()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);
        var moved = await service.CreateOrGetAsync(12345, 1, "inbox/a.md");
        var child = await service.CreateOrGetAsync(12345, 1, "inbox/sub/b.md");
        var elsewhere = await service.CreateOrGetAsync(12345, 1, "notes/c.md");
        var otherRepo = await service.CreateOrGetAsync(12345, 2, "inbox/a.md");

        await service.ReassignOnMoveAsync(12345, 1, "inbox", "archive");

        (await service.GetByTokenAsync(moved.Token))!.NotePath.Should().Be("archive/a.md");
        (await service.GetByTokenAsync(child.Token))!.NotePath.Should().Be("archive/sub/b.md");
        (await service.GetByTokenAsync(elsewhere.Token))!.NotePath.Should().Be("notes/c.md");
        (await service.GetByTokenAsync(otherRepo.Token))!.NotePath.Should().Be("inbox/a.md");
    }

    [Fact]
    public async Task ReassignOnMove_IgnoresPrefixLookalikes()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);
        var lookalike = await service.CreateOrGetAsync(12345, 1, "inboxX/a.md");

        await service.ReassignOnMoveAsync(12345, 1, "inbox", "archive");

        (await service.GetByTokenAsync(lookalike.Token))!.NotePath.Should().Be("inboxX/a.md");
    }

    [Fact]
    public async Task RemoveForPath_RemovesExactAndChildPaths()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);
        var file = await service.CreateOrGetAsync(12345, 1, "inbox/a.md");
        var child = await service.CreateOrGetAsync(12345, 1, "inbox/sub/b.md");
        var elsewhere = await service.CreateOrGetAsync(12345, 1, "notes/c.md");

        await service.RemoveForPathAsync(12345, 1, "inbox");

        (await service.GetByTokenAsync(file.Token)).Should().BeNull();
        (await service.GetByTokenAsync(child.Token)).Should().BeNull();
        (await service.GetByTokenAsync(elsewhere.Token)).Should().NotBeNull();
    }

    // -------------------------------------------------------------------
    // Repository cascade (RepositoriesEndpoints.DeleteRepository)
    // -------------------------------------------------------------------

    [Fact]
    public async Task DeleteRepository_RemovesShareLinksOfTheRepository()
    {
        await using var db = CreateDb();
        var service = new ShareLinksService(db);
        var (user, repository) = await SeedUserWithRepositoryAsync(db);
        var second = new Repository
        {
            TelegramUserId = user.TelegramId,
            DisplayName = "owner/second",
            RepositoryOwner = "owner",
            RepositoryName = "second",
            GitHubToken = _encryptionService.EncryptToken("token_2", TimeSpan.FromMinutes(10)),
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(second);
        await db.SaveChangesAsync();

        var doomed = await service.CreateOrGetAsync(user.TelegramId, repository.Id, "notes/a.md");
        var kept = await service.CreateOrGetAsync(user.TelegramId, second.Id, "notes/a.md");

        var result = await RepositoriesEndpoints.DeleteRepository(repository.Id, null, user.TelegramId, db);

        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult!.StatusCode.Should().Be(200);
        (await service.GetByTokenAsync(doomed.Token)).Should().BeNull("deleted repositories must not leave orphaned links");
        (await service.GetByTokenAsync(kept.Token)).Should().NotBeNull();
    }
}
