using System;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Registration;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Registration;

public class RegistrationEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;

    public RegistrationEndpointsTests()
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

    [Fact]
    public async Task RegisterUser_ShouldReturnBadRequest_WhenRequestIsNull()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await RegistrationEndpoints.RegisterUser(null, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-12345)]
    public async Task RegisterUser_ShouldReturnBadRequest_WhenTelegramIdIsInvalid(long telegramId)
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new RegisterUserRequest(telegramId, "token", "owner", "repo");

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task RegisterUser_ShouldReturnBadRequest_WhenGitHubTokenIsMissing(string? githubToken)
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new RegisterUserRequest(12345, githubToken!, "owner", "repo");

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task RegisterUser_ShouldReturnBadRequest_WhenRepositoryOwnerIsMissing(string? owner)
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new RegisterUserRequest(12345, "token", owner!, "repo");

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task RegisterUser_ShouldReturnBadRequest_WhenRepositoryNameIsMissing(string? repo)
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new RegisterUserRequest(12345, "token", "owner", repo!);

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task RegisterUser_ShouldRegisterNewUser_WhenUserDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new RegisterUserRequest(12345, "github_pat_key", "owner", "repo");

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var user = await db.Users.FirstOrDefaultAsync(u => u.TelegramId == 12345);
        user.Should().NotBeNull();
        user!.RepositoryOwner.Should().Be("owner");
        user.RepositoryName.Should().Be("repo");
        
        var decryptedToken = _encryptionService.DecryptToken(user.GitHubToken!);
        decryptedToken.Should().Be("github_pat_key");

        user.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        user.LastActivityAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RegisterUser_ShouldUpdateExistingUser_WhenUserAlreadyExists()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var existingUser = new User
        {
            TelegramId = 12345,
            GitHubToken = _encryptionService.EncryptToken("old_token", TimeSpan.FromMinutes(10)),
            RepositoryOwner = "old_owner",
            RepositoryName = "old_repo",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastActivityAt = DateTime.UtcNow.AddDays(-1)
        };
        db.Users.Add(existingUser);
        await db.SaveChangesAsync();

        var request = new RegisterUserRequest(12345, "new_token", "new_owner", "new_repo");

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        // Refresh from DB
        var user = await db.Users.FirstOrDefaultAsync(u => u.TelegramId == 12345);
        user.Should().NotBeNull();
        user!.RepositoryOwner.Should().Be("new_owner");
        user.RepositoryName.Should().Be("new_repo");

        var decryptedToken = _encryptionService.DecryptToken(user.GitHubToken!);
        decryptedToken.Should().Be("new_token");

        // CreatedAt should be untouched, LastActivityAt should be updated to current time
        user.CreatedAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(-1), TimeSpan.FromSeconds(5));
        user.LastActivityAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }
}
