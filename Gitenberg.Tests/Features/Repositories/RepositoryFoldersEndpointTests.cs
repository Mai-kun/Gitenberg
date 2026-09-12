using System.Net;
using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Repositories;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Octokit;
using Xunit;
using Repository = Gitenberg.Web.Models.Repository;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Tests.Features.Repositories;

// POST /api/repositories/folders — top-level folder names of a repository,
// used as storage-folder suggestions in the registration and settings forms.
public class RepositoryFoldersEndpointTests
{
    private readonly TokenEncryptionService _encryptionService;
    private readonly MockGitHubService _gitHubService;

    public RepositoryFoldersEndpointTests()
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

    private async Task<(User User, Repository Repository)> SeedUserWithRepositoryAsync(
        AppDbContext db,
        long userId = 12345,
        string owner = "owner",
        string repo = "notes-repo",
        string? token = "stored_token"
    )
    {
        var user = new User
        {
            TelegramId = userId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var repository = new Repository
        {
            TelegramUserId = userId,
            DisplayName = $"{owner}/{repo}",
            RepositoryOwner = owner,
            RepositoryName = repo,
            GitHubToken = token == null ? null : _encryptionService.EncryptToken(token, TimeSpan.FromMinutes(10)),
            InboxPath = "inbox",
            AttachmentsPath = "inbox/attachments",
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        user.SelectedRepositoryId = repository.Id;
        await db.SaveChangesAsync();
        return (user, repository);
    }

    private static RepositoryContent CreateRepositoryContent(string name, string path, ContentType type)
    {
        var constructor = typeof(RepositoryContent).GetConstructors()
            .First(c => c.GetParameters().Length > 0);
        var parameters = constructor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var nameLower = parameters[i].Name?.ToLowerInvariant();
            args[i] = nameLower switch
            {
                "name" => name,
                "path" => path,
                "type" => type,
                "sha" => "sha",
                "size" => 1,
                "url" => "http://dummy/url",
                "giturl" => "http://dummy/giturl",
                "htmlurl" => "http://dummy/htmlurl",
                "downloadurl" => type == ContentType.File ? "http://dummy/raw" : null,
                "encoding" => "utf-8",
                "content" => "dummy content",
                _ => null,
            };
        }

        return (RepositoryContent)constructor.Invoke(args);
    }

    private static List<string> FoldersOf(IResult result)
    {
        var value = (result as IValueHttpResult)!.Value!;
        object GetProp(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)!;
        return ((System.Collections.IEnumerable)GetProp(value, "Folders"))
            .Cast<object>().Select(f => (string)f).ToList();
    }

    // -------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListFolders_ShouldReturnBadRequest_WhenTelegramIdIsMissing()
    {
        await using var db = CreateInMemoryDbContext();
        var request = new RepositoryFoldersRequest("owner", "repo", GitHubToken: "t");

        var result = await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, null, db, _encryptionService, _gitHubService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData(null, "repo")]
    [InlineData("owner", null)]
    [InlineData(" ", "repo")]
    [InlineData("owner", " ")]
    public async Task ListFolders_ShouldReturnBadRequest_WhenOwnerOrRepoIsMissing(string? owner, string? repo)
    {
        await using var db = CreateInMemoryDbContext();
        var request = new RepositoryFoldersRequest(owner!, repo!, GitHubToken: "t");

        var result = await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, 12345, db, _encryptionService, _gitHubService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ListFolders_ShouldReturnBadRequest_WhenNeitherTokenNorRepositoryIdProvided()
    {
        await using var db = CreateInMemoryDbContext();
        var request = new RepositoryFoldersRequest("owner", "repo");

        var result = await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, 12345, db, _encryptionService, _gitHubService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ListFolders_ShouldReturnNotFound_WhenRepositoryIdIsUnknownOrForeign()
    {
        await using var db = CreateInMemoryDbContext();
        var (user, _) = await SeedUserWithRepositoryAsync(db, userId: 12345);
        var (_, foreignRepo) = await SeedUserWithRepositoryAsync(db, userId: 777);

        var unknown = new RepositoryFoldersRequest("owner", "repo", RepositoryId: 99999);
        var resultUnknown = await RepositoriesEndpoints.ListRepositoryFolders(
            unknown, null, user.TelegramId, db, _encryptionService, _gitHubService);

        var foreign = new RepositoryFoldersRequest("owner", "repo", RepositoryId: foreignRepo.Id);
        var resultForeign = await RepositoriesEndpoints.ListRepositoryFolders(
            foreign, null, user.TelegramId, db, _encryptionService, _gitHubService);

        (resultUnknown as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        (resultForeign as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // -------------------------------------------------------------------
    // Folder listing
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListFolders_ShouldReturnOnlyDirectories_Sorted_WithoutDotFolders()
    {
        await using var db = CreateInMemoryDbContext();
        var entries = new List<RepositoryContent>
        {
            CreateRepositoryContent("README.md", "README.md", ContentType.File),
            CreateRepositoryContent("notes", "notes", ContentType.Dir),
            CreateRepositoryContent(".github", ".github", ContentType.Dir),
            CreateRepositoryContent("inbox", "inbox", ContentType.Dir),
            CreateRepositoryContent("docs", "docs", ContentType.Dir),
        };
        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(entries);

        var request = new RepositoryFoldersRequest("owner", "repo", GitHubToken: "t");
        var result = await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, 12345, db, _encryptionService, _gitHubService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status200OK);
        FoldersOf(result).Should().Equal("docs", "inbox", "notes");
    }

    [Fact]
    public async Task ListFolders_ShouldReturnEmptyList_WhenRepositoryHasNoFolders()
    {
        await using var db = CreateInMemoryDbContext();
        var entries = new List<RepositoryContent>
        {
            CreateRepositoryContent("README.md", "README.md", ContentType.File),
        };
        _gitHubService.GetNotesFunc = (_, _) => Task.FromResult<IReadOnlyList<RepositoryContent>>(entries);

        var request = new RepositoryFoldersRequest("owner", "repo", GitHubToken: "t");
        var result = await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, 12345, db, _encryptionService, _gitHubService);

        FoldersOf(result).Should().BeEmpty();
    }

    // -------------------------------------------------------------------
    // Token resolution: explicit token wins, else the stored one
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListFolders_ShouldUseProvidedToken_AndPassOwnerAndRepoToGitHub()
    {
        await using var db = CreateInMemoryDbContext();
        var captured = new List<GitHubRepositoryContext>();
        _gitHubService.GetNotesFunc = (ctx, _) =>
        {
            captured.Add(ctx);
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>());
        };

        var request = new RepositoryFoldersRequest("owner", "repo", GitHubToken: "fresh_token");
        await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, 12345, db, _encryptionService, _gitHubService);

        captured.Should().ContainSingle();
        captured[0].Token.Should().Be("fresh_token");
        captured[0].Owner.Should().Be("owner");
        captured[0].Repo.Should().Be("repo");
    }

    [Fact]
    public async Task ListFolders_ShouldUseDecryptedStoredToken_WhenRepositoryIdProvided()
    {
        await using var db = CreateInMemoryDbContext();
        var (_, repository) = await SeedUserWithRepositoryAsync(db, token: "stored_token");

        var captured = new List<GitHubRepositoryContext>();
        _gitHubService.GetNotesFunc = (ctx, _) =>
        {
            captured.Add(ctx);
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>());
        };

        var request = new RepositoryFoldersRequest("owner", "repo", RepositoryId: repository.Id);
        await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, 12345, db, _encryptionService, _gitHubService);

        captured.Should().ContainSingle();
        captured[0].Token.Should().Be("stored_token");
    }

    [Fact]
    public async Task ListFolders_ShouldPreferProvidedToken_OverStoredToken()
    {
        await using var db = CreateInMemoryDbContext();
        var (_, repository) = await SeedUserWithRepositoryAsync(db, token: "stored_token");

        var captured = new List<GitHubRepositoryContext>();
        _gitHubService.GetNotesFunc = (ctx, _) =>
        {
            captured.Add(ctx);
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>());
        };

        var request = new RepositoryFoldersRequest(
            "owner", "repo", GitHubToken: "fresh_token", RepositoryId: repository.Id);
        await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, 12345, db, _encryptionService, _gitHubService);

        captured.Should().ContainSingle();
        captured[0].Token.Should().Be("fresh_token");
    }

    [Fact]
    public async Task ListFolders_ShouldReturnNotFound_WhenStoredRepositoryHasNoToken()
    {
        await using var db = CreateInMemoryDbContext();
        var (_, repository) = await SeedUserWithRepositoryAsync(db, token: null);

        var request = new RepositoryFoldersRequest("owner", "repo", RepositoryId: repository.Id);
        var result = await RepositoriesEndpoints.ListRepositoryFolders(
            request, null, 12345, db, _encryptionService, _gitHubService);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}
