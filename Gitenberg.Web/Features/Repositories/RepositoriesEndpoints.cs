using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Auth;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Gitenberg.Web.Features.Repositories;

public static class RepositoriesEndpoints
{
    public const int MaxRepositoriesPerUser = 10;

    public static void MapRepositoriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/repositories")
                       .WithTags("Repositories")
                       .RequireAuth();

        group.MapGet("/", ListRepositories)
             .WithName("ListRepositories")
             .WithSummary("All repositories of the user (tokens are never returned)");

        group.MapPost("/", CreateRepository)
             .WithName("CreateRepository")
             .WithSummary("Add a repository (token is stored encrypted)");

        group.MapPut("/{id}", UpdateRepository)
             .WithName("UpdateRepository")
             .WithSummary("Update a repository (omit the token to keep the stored one)");

        group.MapDelete("/{id}", DeleteRepository)
             .WithName("DeleteRepository")
             .WithSummary("Delete a repository with its local search index, pending ops and reminders");

        group.MapPost("/{id}/activate", ActivateRepository)
             .WithName("ActivateRepository")
             .WithSummary("Switch the active repository");

        group.MapPost("/folders", ListRepositoryFolders)
             .WithName("ListRepositoryFolders")
             .WithSummary("Top-level folders of a repository (storage folder suggestions)");
    }

    public static async Task<IResult> ListRepositories(
        AppDbContext dbContext,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repositories = await dbContext.Repositories
            .Where(r => r.TelegramUserId == userId)
            .OrderBy(r => r.Id)
            .ToListAsync();

        return Results.Ok(repositories.Select(r => ToDto(r, user.SelectedRepositoryId)).ToList());
    }

    public static async Task<IResult> CreateRepository(
        [FromBody] CreateRepositoryRequest? request,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        if (request == null)
        {
            return Results.BadRequest(new { Error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            return Results.BadRequest(new { Error = "GitHub token is required." });
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryOwner))
        {
            return Results.BadRequest(new { Error = "Repository owner is required." });
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryName))
        {
            return Results.BadRequest(new { Error = "Repository name is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var count = await dbContext.Repositories.CountAsync(r => r.TelegramUserId == userId);
        if (count >= MaxRepositoriesPerUser)
        {
            return Results.BadRequest(
                new { Error = $"At most {MaxRepositoriesPerUser} repositories per user are allowed." });
        }

        var repository = new Repository
        {
            TelegramUserId = userId.Value,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? $"{request.RepositoryOwner}/{request.RepositoryName}"
                : request.DisplayName.Trim(),
            RepositoryOwner = request.RepositoryOwner,
            RepositoryName = request.RepositoryName,
            GitHubToken = encryptionService.EncryptToken(request.GitHubToken, TimeSpan.FromDays(365)),
            InboxPath = NormalizePath(request.InboxPath) ?? "inbox",
            AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? "inbox/attachments",
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Repositories.Add(repository);
        await dbContext.SaveChangesAsync();

        // The very first repository (e.g. created right after /api/register
        // created the bare user) becomes active automatically. The id only
        // exists after saving.
        if (user.SelectedRepositoryId == null)
        {
            user.SelectedRepositoryId = repository.Id;
            await dbContext.SaveChangesAsync();
        }

        return Results.Ok(ToDto(repository, user.SelectedRepositoryId));
    }

    public static async Task<IResult> UpdateRepository(
        [FromRoute] int id,
        [FromBody] UpdateRepositoryRequest? request,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        if (request == null)
        {
            return Results.BadRequest(new { Error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryOwner))
        {
            return Results.BadRequest(new { Error = "Repository owner is required." });
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryName))
        {
            return Results.BadRequest(new { Error = "Repository name is required." });
        }

        var repository = await dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Id == id && r.TelegramUserId == userId);
        if (repository == null)
        {
            return Results.NotFound(new { Error = $"Repository {id} not found." });
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            repository.DisplayName = request.DisplayName.Trim();
        }
        repository.RepositoryOwner = request.RepositoryOwner;
        repository.RepositoryName = request.RepositoryName;
        repository.InboxPath = NormalizePath(request.InboxPath) ?? repository.InboxPath;
        repository.AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? repository.AttachmentsPath;

        // The token is only updated when provided; otherwise the stored one stays.
        if (!string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            repository.GitHubToken = encryptionService.EncryptToken(request.GitHubToken, TimeSpan.FromDays(365));
        }

        await dbContext.SaveChangesAsync();

        var user = await dbContext.Users.FirstAsync(u => u.TelegramId == userId);
        return Results.Ok(ToDto(repository, user.SelectedRepositoryId));
    }

    public static async Task<IResult> DeleteRepository(
        [FromRoute] int id,
        AppDbContext dbContext,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repository = await dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Id == id && r.TelegramUserId == userId);
        if (repository == null)
        {
            return Results.NotFound(new { Error = $"Repository {id} not found." });
        }

        var total = await dbContext.Repositories.CountAsync(r => r.TelegramUserId == userId);
        if (total <= 1)
        {
            return Results.BadRequest(new { Error = "Cannot delete the last remaining repository." });
        }

        // Cascade cleanup of everything scoped to this repository.
        var uid = userId.Value.ToString();
        var rid = id.ToString();
        await dbContext.IndexedNotes
            .Where(n => n.TelegramUserId == userId && n.RepositoryId == id)
            .ExecuteDeleteAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM NoteSearchFts WHERE TelegramUserId = {uid} AND RepositoryId = {rid}");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM PendingNoteOps WHERE TelegramUserId = {uid} AND RepositoryId = {rid}");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM PinnedItems WHERE TelegramUserId = {uid} AND RepositoryId = {rid}");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM NoteShareLinks WHERE TelegramUserId = {uid} AND RepositoryId = {rid}");

        dbContext.Repositories.Remove(repository);
        await dbContext.SaveChangesAsync();

        // Deleting the active repository activates the first remaining one.
        if (user.SelectedRepositoryId == id)
        {
            user.SelectedRepositoryId = await dbContext.Repositories
                .Where(r => r.TelegramUserId == userId)
                .OrderBy(r => r.Id)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync();
            await dbContext.SaveChangesAsync();
        }

        return Results.Ok(new { Message = $"Repository {id} deleted." });
    }

    public static async Task<IResult> ActivateRepository(
        [FromRoute] int id,
        AppDbContext dbContext,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repository = await dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Id == id && r.TelegramUserId == userId);
        if (repository == null)
        {
            return Results.NotFound(new { Error = $"Repository {id} not found." });
        }

        user.SelectedRepositoryId = id;
        user.LastActivityAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        return Results.Ok(ToDto(repository, user.SelectedRepositoryId));
    }

    // Folder suggestions for the registration/settings forms: the top-level
    // directories of the target repository ("inbox" stays the client default).
    // The token comes either inline from the form (registration, or a newly
    // typed token) or from the stored encrypted token of a saved repository.
    public static async Task<IResult> ListRepositoryFolders(
        [FromBody] RepositoryFoldersRequest? request,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        IGitHubService gitHubService,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        if (request == null)
        {
            return Results.BadRequest(new { Error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryOwner))
        {
            return Results.BadRequest(new { Error = "Repository owner is required." });
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryName))
        {
            return Results.BadRequest(new { Error = "Repository name is required." });
        }

        string? token;
        if (!string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            token = request.GitHubToken;
        }
        else if (request.RepositoryId != null)
        {
            var repository = await dbContext.Repositories
                .FirstOrDefaultAsync(r => r.Id == request.RepositoryId && r.TelegramUserId == userId);
            if (repository?.GitHubToken == null)
            {
                return Results.NotFound(new { Error = $"Repository {request.RepositoryId} not found." });
            }

            token = encryptionService.DecryptToken(repository.GitHubToken);
        }
        else
        {
            return Results.BadRequest(new { Error = "GitHub token is required." });
        }

        var context = new GitHubRepositoryContext(token, request.RepositoryOwner, request.RepositoryName);
        var contents = await gitHubService.GetNotesAsync(context);

        var folders = contents
            .Where(item => item.Type == Octokit.ContentType.Dir && !item.Name.StartsWith("."))
            .Select(item => item.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(new { Folders = folders });
    }

    private static object ToDto(Repository repository, int? selectedRepositoryId) => new
    {
        Id = repository.Id,
        repository.DisplayName,
        repository.RepositoryOwner,
        repository.RepositoryName,
        HasToken = !string.IsNullOrWhiteSpace(repository.GitHubToken),
        repository.InboxPath,
        repository.AttachmentsPath,
        IsActive = repository.Id == selectedRepositoryId,
    };

    // Optional folder settings are trimmed of slashes/whitespace; an empty
    // result keeps the stored (or default) value.
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('/');
        return trimmed.Length == 0 ? null : trimmed;
    }
}
