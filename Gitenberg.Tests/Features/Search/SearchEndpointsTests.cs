using System.Net;
using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Search;

public class SearchEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;

    public SearchEndpointsTests()
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
        return dbContext;
    }

    private async Task SeedUserAsync(AppDbContext db, long telegramId)
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
    }

    private static Task InsertFtsRowAsync(AppDbContext db, long telegramUserId, string notePath, string content)
    {
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO NoteSearchFts (TelegramUserId, NotePath, Content) VALUES ({telegramUserId.ToString()}, {notePath}, {content})"
        );
    }

    private static List<NoteSearchResult> GetResultsValue(IResult result)
    {
        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        return valueResult!.Value.Should().BeAssignableTo<List<NoteSearchResult>>().Subject;
    }

    [Fact]
    public async Task SearchNotes_ShouldReturnBadRequest_WhenTelegramIdIsNull()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await SearchEndpoints.SearchNotes("kernel", null, null, db);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SearchNotes_ShouldReturnBadRequest_WhenQueryIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);

        // Act
        var result = await SearchEndpoints.SearchNotes(null, 12345, null, db);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SearchNotes_ShouldReturnBadRequest_WhenQueryIsWhitespace()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);

        // Act
        var result = await SearchEndpoints.SearchNotes("   ", 12345, null, db);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SearchNotes_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await SearchEndpoints.SearchNotes("kernel", 12345, null, db);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task SearchNotes_ShouldReturnNotePathAndHighlightedSnippet_WhenNoteMatches()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await InsertFtsRowAsync(
            db,
            12345,
            "notes/os-basics.md",
            "This note explains the kernel scheduling algorithm used by the operating system in great detail."
        );

        // Act
        var result = await SearchEndpoints.SearchNotes("kernel", 12345, null, db);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var results = GetResultsValue(result);
        results.Should().ContainSingle();

        var match = results[0];
        match.NotePath.Should().Be("notes/os-basics.md");
        match.Snippet.Should().Contain("<b>kernel</b>");
    }

    [Fact]
    public async Task SearchNotes_ShouldReturnEmptyList_WhenNoNoteMatches()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await InsertFtsRowAsync(db, 12345, "notes/os-basics.md", "A note about completely different topics.");

        // Act
        var result = await SearchEndpoints.SearchNotes("kernel", 12345, null, db);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        GetResultsValue(result).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchNotes_ShouldOnlyReturnNotesOfTheRequestingUser()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await SeedUserAsync(db, 67890);
        await InsertFtsRowAsync(db, 12345, "notes/mine.md", "A note about kernel scheduling.");
        await InsertFtsRowAsync(db, 67890, "notes/other-user.md", "Another note about kernel scheduling.");

        // Act
        var result = await SearchEndpoints.SearchNotes("kernel", 12345, null, db);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var results = GetResultsValue(result);
        results.Should().ContainSingle();
        results[0].NotePath.Should().Be("notes/mine.md");
    }

    [Fact]
    public async Task SearchNotes_ShouldReturnBadRequest_WhenFtsQuerySyntaxIsInvalid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserAsync(db, 12345);
        await InsertFtsRowAsync(db, 12345, "notes/os-basics.md", "Some content.");

        // Act - an unbalanced quote is invalid FTS5 MATCH syntax.
        var result = await SearchEndpoints.SearchNotes("\"", 12345, null, db);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
