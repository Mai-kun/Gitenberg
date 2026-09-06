using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Export;
using Gitenberg.Web.Services;
using Gitenberg.Tests.Mocks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Export;

public class ExportEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly MockGitHubService _gitHubService = new();

    public ExportEndpointsTests()
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
        return dbContext;
    }

    private async Task<AppDbContext> CreateDbContextWithUserAsync(long telegramId, string? githubToken)
    {
        var db = CreateInMemoryDbContext();
        db.Users.Add(new User
        {
            TelegramId = telegramId,
            GitHubToken = githubToken is null ? null : _encryptionService.EncryptToken(githubToken, TimeSpan.FromMinutes(10)),
            RepositoryOwner = "owner",
            RepositoryName = "my-notes",
        });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task DownloadArchive_ShouldReturnBadRequest_WhenTelegramIdIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await ExportEndpoints.DownloadArchive(
            null, null, null, db, _gitHubService, _encryptionService);

        // Assert
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _gitHubService.ArchiveRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadArchive_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await ExportEndpoints.DownloadArchive(
            12345, null, null, db, _gitHubService, _encryptionService);

        // Assert
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _gitHubService.ArchiveRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadArchive_ShouldReturnBadRequest_WhenGitHubTokenIsMissing()
    {
        // Arrange
        await using var db = await CreateDbContextWithUserAsync(12345, githubToken: null);

        // Act
        var result = await ExportEndpoints.DownloadArchive(
            12345, null, null, db, _gitHubService, _encryptionService);

        // Assert
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _gitHubService.ArchiveRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadArchive_ShouldReturnZipFile_WhenUserIsConfigured()
    {
        // Arrange
        var archiveBytes = Encoding.UTF8.GetBytes("fake-zip-payload");
        _gitHubService.GetRepositoryArchiveFunc = (_, _) => Task.FromResult(archiveBytes);
        await using var db = await CreateDbContextWithUserAsync(12345, "github_token");

        // Act
        var result = await ExportEndpoints.DownloadArchive(
            12345, null, null, db, _gitHubService, _encryptionService);

        // Assert
        var fileResult = result.Should().BeAssignableTo<FileContentHttpResult>().Subject;
        fileResult.ContentType.Should().Be("application/zip");
        fileResult.FileDownloadName.Should().StartWith("my-notes-").And.EndWith(".zip");

        fileResult.FileContents.ToArray().SequenceEqual(archiveBytes).Should().BeTrue();

        var (context, reference) = _gitHubService.ArchiveRequests.Single();
        context.Owner.Should().Be("owner");
        context.Repo.Should().Be("my-notes");
        context.Token.Should().Be("github_token");
        reference.Should().BeNull();
    }
}
