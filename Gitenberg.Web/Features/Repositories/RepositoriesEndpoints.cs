using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Repositories;

/// <summary>
/// Multi-repo management: a user keeps several GitHub repositories
/// ("Personal", "Work") and switches the active one. Notes, search, the bot
/// and sync always operate on the active repository; pending ops and the
/// search index stay scoped per repository, so switching is safe at any time.
/// </summary>
public static class RepositoriesEndpoints
{
    public const int MaxRepositoriesPerUser = 10;

    public static void MapRepositoriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/repositories")
                       .WithTags("Repositories")
                       .RequireTelegramAuth();

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
    }

    public static async Task<IResult> ListRepositories(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var repositories = await dbContext.Repositories
            .Where(r => r.TelegramUserId == telegramId)
            .OrderBy(r => r.Id)
            .ToListAsync();

        return Results.Ok(repositories.Select(r => ToDto(r, user.SelectedRepositoryId)).ToList());
    }

    public static async Task<IResult> CreateRepository(
        [FromBody] CreateRepositoryRequest? request,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
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

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var count = await dbContext.Repositories.CountAsync(r => r.TelegramUserId == telegramId);
        if (count >= MaxRepositoriesPerUser)
        {
            return Results.BadRequest(
                new { Error = $"At most {MaxRepositoriesPerUser} repositories per user are allowed." });
        }

        var repository = new Repository
        {
            TelegramUserId = telegramId.Value,
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
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
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
            .FirstOrDefaultAsync(r => r.Id == id && r.TelegramUserId == telegramId);
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

        var user = await dbContext.Users.FirstAsync(u => u.TelegramId == telegramId);
        return Results.Ok(ToDto(repository, user.SelectedRepositoryId));
    }

    public static async Task<IResult> DeleteRepository(
        [FromRoute] int id,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var repository = await dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Id == id && r.TelegramUserId == telegramId);
        if (repository == null)
        {
            return Results.NotFound(new { Error = $"Repository {id} not found." });
        }

        var total = await dbContext.Repositories.CountAsync(r => r.TelegramUserId == telegramId);
        if (total <= 1)
        {
            return Results.BadRequest(new { Error = "Cannot delete the last remaining repository." });
        }

        // Cascade cleanup of everything scoped to this repository.
        var uid = telegramId.Value.ToString();
        var rid = id.ToString();
        await dbContext.IndexedNotes
            .Where(n => n.TelegramUserId == telegramId && n.RepositoryId == id)
            .ExecuteDeleteAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM NoteSearchFts WHERE TelegramUserId = {uid} AND RepositoryId = {rid}");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM PendingNoteOps WHERE TelegramUserId = {uid} AND RepositoryId = {rid}");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM Reminders WHERE TelegramUserId = {uid} AND RepositoryId = {rid}");
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
                .Where(r => r.TelegramUserId == telegramId)
                .OrderBy(r => r.Id)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync();
            await dbContext.SaveChangesAsync();
        }

        return Results.Ok(new { Message = $"Repository {id} deleted." });
    }

    public static async Task<IResult> ActivateRepository(
        [FromRoute] int id,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var repository = await dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Id == id && r.TelegramUserId == telegramId);
        if (repository == null)
        {
            return Results.NotFound(new { Error = $"Repository {id} not found." });
        }

        user.SelectedRepositoryId = id;
        user.LastActivityAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        return Results.Ok(ToDto(repository, user.SelectedRepositoryId));
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
