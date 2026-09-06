using System.Globalization;
using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Pins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gitenberg.Tests.Features.Pins;

public class PinsServiceTests
{
    private static AppDbContext CreateDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
                      .UseSqlite(connection)
                      .Options;

        var dbContext = new AppDbContext(options);
        dbContext.Database.EnsureCreated();
        PinsService.EnsureTableCreated(dbContext);
        return dbContext;
    }

    private static async Task<List<string>> ReadPathsAsync(AppDbContext db)
    {
        var rows = await db.Database.SqlQuery<PinPathRow>(
            $"SELECT ItemPath, PinnedAt FROM PinnedItems ORDER BY ItemPath"
        ).ToListAsync();
        return rows.Select(r => r.ItemPath).ToList();
    }

    private sealed record PinPathRow(string ItemPath, string PinnedAt);

    // -------------------------------------------------------------------
    // PinAsync / UnpinAsync / ListAsync
    // -------------------------------------------------------------------

    [Fact]
    public async Task PinAsync_PersistsPath_AndListReturnsIt()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);

        await service.PinAsync(12345, 1, "inbox/today.md");

        var pins = await service.ListAsync(12345, 1);
        pins.Should().ContainSingle().Which.ItemPath.Should().Be("inbox/today.md");
        pins[0].PinnedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task PinAsync_IsIdempotent()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);

        await service.PinAsync(12345, 1, "inbox/today.md");
        await service.PinAsync(12345, 1, "inbox/today.md");

        (await ReadPathsAsync(db)).Should().ContainSingle("INSERT OR REPLACE must not duplicate the keyed row");
    }

    [Fact]
    public async Task PinAsync_NormalizesSlashes()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);

        await service.PinAsync(12345, 1, "/inbox/today.md/");

        (await ReadPathsAsync(db)).Should().ContainSingle().Which.Should().Be("inbox/today.md");
    }

    [Fact]
    public async Task UnpinAsync_RemovesRow_AndToleratesUnknownPath()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);
        await service.PinAsync(12345, 1, "inbox/today.md");

        await service.UnpinAsync(12345, 1, "inbox/other.md");
        (await ReadPathsAsync(db)).Should().HaveCount(1, "unpinning an unknown path changes nothing");

        await service.UnpinAsync(12345, 1, "inbox/today.md");
        (await ReadPathsAsync(db)).Should().BeEmpty();
    }

    // -------------------------------------------------------------------
    // ReassignOnMoveAsync
    // -------------------------------------------------------------------

    [Fact]
    public async Task ReassignOnMove_RewritesExactAndChildPaths_OnlyForTheRepository()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);
        await service.PinAsync(12345, 1, "inbox/today.md");
        await service.PinAsync(12345, 1, "inbox/sub/a.md");
        await service.PinAsync(12345, 1, "other.md");
        await service.PinAsync(12345, 2, "inbox/keep.md"); // another repository of the same user

        await service.ReassignOnMoveAsync(12345, 1, "inbox", "work");

        (await service.ListAsync(12345, 1)).Select(p => p.ItemPath).Should().Equal(
            "other.md",
            "work/sub/a.md",
            "work/today.md");
        (await service.ListAsync(12345, 2)).Select(p => p.ItemPath).Should().Equal("inbox/keep.md");
    }

    [Fact]
    public async Task ReassignOnMove_DoesNotMatchSiblingWithSamePrefix()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);
        await service.PinAsync(12345, 1, "inbox2/x.md");

        await service.ReassignOnMoveAsync(12345, 1, "inbox", "work");

        (await service.ListAsync(12345, 1)).Select(p => p.ItemPath).Should().Equal("inbox2/x.md");
    }

    [Fact]
    public async Task ReassignOnMove_SamePath_IsNoOp()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);
        await service.PinAsync(12345, 1, "inbox/a.md");

        await service.ReassignOnMoveAsync(12345, 1, "inbox/a.md", "inbox/a.md");

        (await service.ListAsync(12345, 1)).Select(p => p.ItemPath).Should().Equal("inbox/a.md");
    }

    // -------------------------------------------------------------------
    // RemoveForPathAsync
    // -------------------------------------------------------------------

    [Fact]
    public async Task RemoveForPath_RemovesExactAndChildPaths()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);
        await service.PinAsync(12345, 1, "notes/deleted/a.md");
        await service.PinAsync(12345, 1, "notes/deleted.md");
        await service.PinAsync(12345, 1, "notes/keep.md");

        await service.RemoveForPathAsync(12345, 1, "notes/deleted");

        // The sibling file "notes/deleted.md" shares the prefix name but not the
        // folder — it must survive.
        (await service.ListAsync(12345, 1)).Select(p => p.ItemPath).Should().Equal("notes/deleted.md", "notes/keep.md");
    }

    [Fact]
    public async Task RemoveForPath_IsScopedToRepository()
    {
        await using var db = CreateDb();
        var service = new PinsService(db);
        await service.PinAsync(12345, 1, "inbox/a.md");
        await service.PinAsync(12345, 2, "inbox/a.md");

        await service.RemoveForPathAsync(12345, 1, "inbox/a.md");

        (await service.ListAsync(12345, 2)).Should().ContainSingle("pins of other repositories stay");
    }
}
