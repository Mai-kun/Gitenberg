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
using Repository = Gitenberg.Web.Models.Repository;
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

        var repository = await db.Repositories.SingleAsync(r => r.TelegramUserId == 12345);
        repository.RepositoryOwner.Should().Be("owner");
        repository.RepositoryName.Should().Be("repo");

        var decryptedToken = _encryptionService.DecryptToken(repository.GitHubToken!);
        decryptedToken.Should().Be("github_pat_key");

        var user = await db.Users.FirstAsync(u => u.TelegramId == 12345);
        user.SelectedRepositoryId.Should().Be(repository.Id);
        repository.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        user.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        user.LastActivityAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RegisterUser_ShouldUpdateExistingUser_WhenUserAlreadyExists()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var (user, repository) = await SeedUserWithRepositoryAsync(db, 12345, token: "old_token", owner: "old_owner", repo: "old_repo");

        var request = new RegisterUserRequest(12345, "new_token", "new_owner", "new_repo");

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        // Refresh from DB: settings updates go to the active repository.
        db.Entry(repository).Reload();
        repository.RepositoryOwner.Should().Be("new_owner");
        repository.RepositoryName.Should().Be("new_repo");

        var decryptedToken = _encryptionService.DecryptToken(repository.GitHubToken!);
        decryptedToken.Should().Be("new_token");

        // CreatedAt should be untouched, LastActivityAt should be updated to current time
        user.CreatedAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(-1), TimeSpan.FromSeconds(5));
        db.Entry(user).Reload();
        user.LastActivityAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RegisterUser_ShouldSaveCustomPaths_WhenProvided()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new RegisterUserRequest(
            12345, "token", "owner", "repo",
            InboxPath: "/my-notes/",
            AttachmentsPath: " assets/photos "
        );

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var repository = await db.Repositories.SingleAsync(r => r.TelegramUserId == 12345);
        repository.InboxPath.Should().Be("my-notes");
        repository.AttachmentsPath.Should().Be("assets/photos");
    }

    [Fact]
    public async Task RegisterUser_ShouldUseDefaultPaths_WhenPathsAreOmitted()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new RegisterUserRequest(12345, "token", "owner", "repo");

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var repository = await db.Repositories.SingleAsync(r => r.TelegramUserId == 12345);
        repository.InboxPath.Should().Be("inbox");
        repository.AttachmentsPath.Should().Be("inbox/attachments");
    }

    [Fact]
    public async Task RegisterUser_ShouldKeepExistingPaths_WhenPathsAreOmittedOnUpdate()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserWithRepositoryAsync(db, 12345, token: "old_token", owner: "old_owner", repo: "old_repo",
            inboxPath: "my-notes", attachmentsPath: "assets/photos");

        var request = new RegisterUserRequest(12345, "new_token", "new_owner", "new_repo");

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var repository = await db.Repositories.SingleAsync(r => r.TelegramUserId == 12345);
        repository.InboxPath.Should().Be("my-notes");
        repository.AttachmentsPath.Should().Be("assets/photos");
    }

    [Fact]
    public async Task RegisterUser_ShouldUpdatePaths_WhenNewPathsProvidedOnUpdate()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        await SeedUserWithRepositoryAsync(db, 12345, token: "old_token", owner: "old_owner", repo: "old_repo",
            inboxPath: "my-notes", attachmentsPath: "assets/photos");

        var request = new RegisterUserRequest(
            12345, "new_token", "new_owner", "new_repo",
            InboxPath: "notes",
            AttachmentsPath: "notes/media"
        );

        // Act
        var result = await RegistrationEndpoints.RegisterUser(request, db, _encryptionService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var repository = await db.Repositories.SingleAsync(r => r.TelegramUserId == 12345);
        repository.InboxPath.Should().Be("notes");
        repository.AttachmentsPath.Should().Be("notes/media");
    }

    private async Task<(User User, Repository Repository)> SeedUserWithRepositoryAsync(
        AppDbContext db,
        long telegramId,
        string token,
        string owner,
        string repo,
        string inboxPath = "inbox",
        string attachmentsPath = "inbox/attachments"
    )
    {
        var user = new User
        {
            TelegramId = telegramId,
            GitHubToken = _encryptionService.EncryptToken(token, TimeSpan.FromMinutes(10)),
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastActivityAt = DateTime.UtcNow.AddDays(-1),
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
            InboxPath = inboxPath,
            AttachmentsPath = attachmentsPath,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();
        return (user, repository);
    }
}
