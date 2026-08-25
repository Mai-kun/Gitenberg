using System.Collections;
using System.Net;
using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Notes;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octokit;
using Xunit;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Notes;

public class NotesEndpointsTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly MockGitHubService _gitHubService;

    public NotesEndpointsTests()
    {
        var provider = new EphemeralDataProtectionProvider();
        _encryptionService = new TokenEncryptionService(provider);
        _gitHubService = new MockGitHubService();
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

    private static RepositoryContent CreateRepositoryContent(
        string name,
        string path,
        string sha,
        int size,
        ContentType type,
        string downloadUrl,
        string htmlUrl
    )
    {
        var constructor = typeof(RepositoryContent).GetConstructors()[0];
        var parameters = constructor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var nameLower = parameters[i].Name?.ToLowerInvariant();
            args[i] = nameLower switch
            {
                "name" => name,
                "path" => path,
                "sha" => sha,
                "size" => size,
                "type" => type,
                "downloadurl" => downloadUrl,
                "htmlurl" => htmlUrl,
                "url" => "http://dummy/url",
                "giturl" => "http://dummy/giturl",
                "encoding" => "utf-8",
                "content" => "dummy content",
                _ => null,
            };
        }

        return (RepositoryContent)constructor.Invoke(args);
    }

    [Fact]
    public async Task GetNotes_ShouldReturnBadRequest_WhenTelegramIdIsNull()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await NotesEndpoints.GetNotes(null, null, null, db, _encryptionService, _gitHubService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNotes_ShouldReturnNotFound_WhenUserDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await NotesEndpoints.GetNotes(null, 12345, null, db, _encryptionService, _gitHubService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetNotes_ShouldReturnBadRequest_WhenGitHubTokenIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = null,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Act
        var result = await NotesEndpoints.GetNotes(null, 12345, null, db, _encryptionService, _gitHubService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNotes_ShouldReturnOkWithNotes_WhenRequestIsValid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var noteContent = CreateRepositoryContent(
            "note1.md",
            "notes/note1.md",
            "sha123",
            100,
            ContentType.File,
            "http://download",
            "http://html"
        );
        _gitHubService.GetNotesFunc = (ctx, path) =>
        {
            ctx.Token.Should().Be("pat_123");
            ctx.Owner.Should().Be("owner");
            ctx.Repo.Should().Be("repo");
            path.Should().BeNull();
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { noteContent });
        };

        // Act
        var result = await NotesEndpoints.GetNotes(null, null, 12345, db, _encryptionService, _gitHubService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();
        valueResult.Value.Should().BeAssignableTo<IEnumerable>();
    }

    [Fact]
    public async Task GetNotes_ShouldPassPathToService_WhenPathIsProvided()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var noteContent = CreateRepositoryContent(
            "subnote.md",
            "subfolder/subnote.md",
            "sha456",
            150,
            ContentType.File,
            "http://download/subfolder",
            "http://html/subfolder"
        );

        var pathCaptured = string.Empty;
        _gitHubService.GetNotesFunc = (ctx, path) =>
        {
            pathCaptured = path;
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent> { noteContent });
        };

        // Act
        var result = await NotesEndpoints.GetNotes("subfolder", null, 12345, db, _encryptionService, _gitHubService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        pathCaptured.Should().Be("subfolder");
    }

    [Fact]
    public async Task GetNotes_ShouldThrowException_WhenGitHubServiceThrowsException()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        _gitHubService.GetNotesFunc = (_, _) => throw new Exception("GitHub API down");

        // Act
        Func<Task> act = () => NotesEndpoints.GetNotes(null, 12345, null, db, _encryptionService, _gitHubService);

        // Assert
        await act.Should().ThrowAsync<Exception>().WithMessage("GitHub API down");
    }

    [Fact]
    public async Task GetNoteContent_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await NotesEndpoints.GetNoteContent(null!, 12345, null, db, _encryptionService, _gitHubService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task GetNoteContent_ShouldThrowNotFoundException_WhenNoteDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        _gitHubService.GetNoteContentFunc = (_, _) =>
            throw new NotFoundException("Not found", HttpStatusCode.NotFound);

        // Act
        Func<Task> act = () => NotesEndpoints.GetNoteContent(
            "notes/missing.md",
            12345,
            null,
            db,
            _encryptionService,
            _gitHubService
        );

        // Assert
        await act.Should().ThrowAsync<NotFoundException>().WithMessage("Not found");
    }

    [Fact]
    public async Task GetNoteContent_ShouldReturnOkWithContent_WhenRequestIsValid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        _gitHubService.GetNoteContentFunc = (_, _) => Task.FromResult("Hello world note content");

        // Act
        var result = await NotesEndpoints.GetNoteContent(
            "notes/note1.md",
            12345,
            null,
            db,
            _encryptionService,
            _gitHubService
        );

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var valueResult = result as IValueHttpResult;
        valueResult.Should().NotBeNull();

        // Extract properties using reflection or casting to check content
        var value = valueResult.Value;
        value.Should().NotBeNull();

        var contentProp = value.GetType().GetProperty("Content");
        contentProp.Should().NotBeNull();
        contentProp.GetValue(value).Should().Be("Hello world note content");
    }

    [Fact]
    public async Task CreateOrUpdateNote_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var request = new CreateOrUpdateNoteRequest(null!, "content", "msg");

        // Act
        var result = await NotesEndpoints.CreateOrUpdateNote(
            request,
            12345,
            null,
            db,
            _encryptionService,
            _gitHubService
        );

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task CreateOrUpdateNote_ShouldReturnOk_WhenRequestIsValid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var requestCalled = false;
        _gitHubService.CreateOrUpdateNoteFunc = (ctx, path, content, msg) =>
        {
            ctx.Token.Should().Be("pat_123");
            path.Should().Be("notes/new.md");
            content.Should().Be("hello");
            msg.Should().Be("Create/Update message");
            requestCalled = true;
            return Task.CompletedTask;
        };

        var request = new CreateOrUpdateNoteRequest("notes/new.md", "hello", "Create/Update message");

        // Act
        var result = await NotesEndpoints.CreateOrUpdateNote(
            request,
            12345,
            null,
            db,
            _encryptionService,
            _gitHubService
        );

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        requestCalled.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteNote_ShouldReturnBadRequest_WhenPathIsMissing()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();

        // Act
        var result = await NotesEndpoints.DeleteNote(null!, "msg", 12345, null, db, _encryptionService, _gitHubService);

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task DeleteNote_ShouldThrowNotFoundException_WhenNoteDoesNotExist()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        _gitHubService.DeleteNoteFunc = (_, _, _) =>
            throw new NotFoundException("Not found", HttpStatusCode.NotFound);

        // Act
        Func<Task> act = () => NotesEndpoints.DeleteNote(
            "notes/missing.md",
            "delete msg",
            12345,
            null,
            db,
            _encryptionService,
            _gitHubService
        );

        // Assert
        await act.Should().ThrowAsync<NotFoundException>().WithMessage("Not found");
    }

    [Fact]
    public async Task DeleteNote_ShouldReturnOk_WhenRequestIsValid()
    {
        // Arrange
        await using var db = CreateInMemoryDbContext();
        var encryptedToken = _encryptionService.EncryptToken("pat_123", TimeSpan.FromMinutes(10));
        var user = new User
        {
            TelegramId = 12345,
            GitHubToken = encryptedToken,
            RepositoryOwner = "owner",
            RepositoryName = "repo",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var deleteCalled = false;
        _gitHubService.DeleteNoteFunc = (ctx, path, msg) =>
        {
            ctx.Token.Should().Be("pat_123");
            path.Should().Be("notes/delete.md");
            msg.Should().Be("delete msg");
            deleteCalled = true;
            return Task.CompletedTask;
        };

        // Act
        var result = await NotesEndpoints.DeleteNote(
            "notes/delete.md",
            "delete msg",
            12345,
            null,
            db,
            _encryptionService,
            _gitHubService
        );

        // Assert
        var statusCodeResult = result as IStatusCodeHttpResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status200OK);
        deleteCalled.Should().BeTrue();
    }
}