using System.Text.Json;
using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Activity;
using Gitenberg.Web.Features.Notes;
using Gitenberg.Web.Features.Pins;
using Gitenberg.Web.Features.Reminders;
using Gitenberg.Web.Features.Repositories;
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
using Xunit;
using Repository = Gitenberg.Web.Models.Repository;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Pins;

public class PinsEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly IMemoryCache _memoryCache;

    public PinsEndpointsTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    private static AppDbContext CreateDb()
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
        ActivityService.EnsureTableCreated(dbContext);
        PinsService.EnsureTableCreated(dbContext);
        dbContext.EnsureFtsTableCreated();
        return dbContext;
    }

    private IRepositoryContextResolver CreateResolver(AppDbContext db) =>
        new RepositoryContextResolver(db, _encryptionService, NullLogger<RepositoryContextResolver>.Instance);

    private async Task<Repository> SeedUserWithRepositoryAsync(AppDbContext db, long telegramId = 12345)
    {
        db.Users.Add(new User
        {
            TelegramId = telegramId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var repository = new Repository
        {
            TelegramUserId = telegramId,
            DisplayName = "owner/first",
            RepositoryOwner = "owner",
            RepositoryName = "first",
            GitHubToken = _encryptionService.EncryptToken("token_1", TimeSpan.FromMinutes(10)),
            InboxPath = "inbox",
            AttachmentsPath = "inbox/attachments",
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var user = await db.Users.FirstAsync(u => u.TelegramId == telegramId);
        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();
        return repository;
    }

    private static List<string> PinPaths(IResult result)
    {
        var value = (result as IValueHttpResult)!.Value.Should().BeAssignableTo<List<object>>().Subject;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<List<PinDto>>(json)!
            .Select(p => p.Path)
            .ToList();
    }

    private sealed record PinDto(string Path, DateTime PinnedAt);

    // -------------------------------------------------------------------
    // Auth / validation
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListPins_ShouldReturnBadRequest_WhenTelegramIdIsMissing()
    {
        await using var db = CreateDb();

        var result = await PinsEndpoints.ListPins(null, null, db, CreateResolver(db), new PinsService(db));

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ListPins_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        await using var db = CreateDb();

        var result = await PinsEndpoints.ListPins(null, 12345, db, CreateResolver(db), new PinsService(db));

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ListPins_ShouldReturnBadRequest_WhenNoRepositoryIsConfigured()
    {
        await using var db = CreateDb();
        db.Users.Add(new User { TelegramId = 12345, CreatedAt = DateTime.UtcNow, LastActivityAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await PinsEndpoints.ListPins(null, 12345, db, CreateResolver(db), new PinsService(db));

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task PinItem_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        await using var db = CreateDb();
        await SeedUserWithRepositoryAsync(db);
        var service = new PinsService(db);

        var result = await PinsEndpoints.PinItem(new PinItemRequest("   "), null, 12345, db, CreateResolver(db), service);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // -------------------------------------------------------------------
    // PUT → GET → DELETE happy path
    // -------------------------------------------------------------------

    [Fact]
    public async Task Pin_List_Unpin_Roundtrip_Works()
    {
        await using var db = CreateDb();
        await SeedUserWithRepositoryAsync(db);
        var service = new PinsService(db);
        var resolver = CreateResolver(db);

        var pinned = await PinsEndpoints.PinItem(new PinItemRequest("/inbox/today.md/"), null, 12345, db, resolver, service);
        (pinned as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status200OK);

        var list = await PinsEndpoints.ListPins(null, 12345, db, resolver, service);
        PinPaths(list).Should().Equal("inbox/today.md");

        var unpinned = await PinsEndpoints.UnpinItem("inbox/today.md", null, 12345, db, resolver, service);
        (unpinned as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status200OK);

        var empty = await PinsEndpoints.ListPins(null, 12345, db, resolver, service);
        PinPaths(empty).Should().BeEmpty();
    }

    // -------------------------------------------------------------------
    // Move / delete re-point the pins of the active repository
    // -------------------------------------------------------------------

    [Fact]
    public async Task MoveNote_ReassignsPinnedPath()
    {
        await using var db = CreateDb();
        await SeedUserWithRepositoryAsync(db);
        var service = new PinsService(db);
        await service.PinAsync(12345, 1, "inbox/today.md");

        await NotesEndpoints.MoveNote(
            new MoveNoteRequest("inbox/today.md", "work/today.md", null, null),
            12345,
            null,
            null,
            db,
            CreateResolver(db),
            CreatePendingSync(db),
            _memoryCache,
            new ReminderService(db),
            new ActivityService(db),
            service);

        (await service.ListAsync(12345, 1)).Select(p => p.ItemPath).Should().Equal("work/today.md");
    }

    [Fact]
    public async Task DeleteNote_UnpinsExactAndChildPaths()
    {
        await using var db = CreateDb();
        await SeedUserWithRepositoryAsync(db);
        var service = new PinsService(db);
        await service.PinAsync(12345, 1, "notes/deleted/a.md");
        await service.PinAsync(12345, 1, "notes/keep.md");

        await NotesEndpoints.DeleteNote(
            "notes/deleted",
            null,
            12345,
            null,
            null,
            db,
            CreateResolver(db),
            CreatePendingSync(db),
            _memoryCache,
            new ReminderService(db),
            new ActivityService(db),
            service);

        (await service.ListAsync(12345, 1)).Select(p => p.ItemPath).Should().Equal("notes/keep.md");
    }

    // -------------------------------------------------------------------
    // Repository deletion cascades to its pins
    // -------------------------------------------------------------------

    [Fact]
    public async Task DeleteRepository_CascadesToPins_OfThatRepositoryOnly()
    {
        await using var db = CreateDb();
        var first = await SeedUserWithRepositoryAsync(db);
        var second = new Repository
        {
            TelegramUserId = 12345,
            DisplayName = "owner/second",
            RepositoryOwner = "owner",
            RepositoryName = "second",
            GitHubToken = _encryptionService.EncryptToken("token_2", TimeSpan.FromMinutes(10)),
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(second);
        await db.SaveChangesAsync();

        var service = new PinsService(db);
        await service.PinAsync(12345, first.Id, "inbox/first.md");
        await service.PinAsync(12345, second.Id, "inbox/second.md");

        await RepositoriesEndpoints.DeleteRepository(first.Id, null, 12345, db);

        (await service.ListAsync(12345, first.Id)).Should().BeEmpty();
        (await service.ListAsync(12345, second.Id)).Select(p => p.ItemPath).Should().Equal("inbox/second.md");
    }

    private static PendingSyncService CreatePendingSync(AppDbContext db)
    {
        PendingSyncService.EnsureTableCreated(db);
        return new PendingSyncService(db);
    }
}
