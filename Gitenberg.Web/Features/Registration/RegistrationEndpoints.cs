using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Registration;

public static class RegistrationEndpoints
{
    public static void MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/register")
                       .WithTags("Registration")
                       .RequireTelegramAuth();

        group.MapPost("/", RegisterUser)
             .WithName("RegisterUser")
             .WithSummary("Register or update a user (operates on the active repository)");

        group.MapGet("/", GetSettings)
             .WithName("GetSettings")
             .WithSummary("Active repository settings (token is never returned)");
    }

    public static async Task<IResult> GetSettings(
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

        var repository = await ResolveRepositoryRowAsync(dbContext, user);
        if (repository == null)
        {
            return Results.Ok(new
            {
                RepositoryId = (int?)null,
                DisplayName = (string?)null,
                RepositoryOwner = string.Empty,
                RepositoryName = string.Empty,
                HasToken = false,
                InboxPath = "inbox",
                AttachmentsPath = "inbox/attachments",
            });
        }

        return Results.Ok(new
        {
            RepositoryId = (int?)repository.Id,
            repository.DisplayName,
            repository.RepositoryOwner,
            repository.RepositoryName,
            HasToken = !string.IsNullOrWhiteSpace(repository.GitHubToken),
            repository.InboxPath,
            repository.AttachmentsPath,
        });
    }

    public static async Task<IResult> RegisterUser(
        [FromBody] RegisterUserRequest? request,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId = null,
        [FromQuery(Name = "telegramId")] long? queryTelegramId = null,
        HttpContext? httpContext = null
    )
    {
        if (request == null)
        {
            return Results.BadRequest(new { Error = "Request body is required." });
        }

        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId)
                         ?? request.TelegramId;

        if (telegramId <= 0)
        {
            return Results.BadRequest(new { Error = "Telegram ID must be greater than zero." });
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

        string? encryptedToken = null;
        if (!string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            encryptedToken = encryptionService.EncryptToken(request.GitHubToken, TimeSpan.FromDays(365));
        }

        if (user == null)
        {
            // First registration: the token is mandatory and becomes the
            // user's first (active) repository.
            if (encryptedToken == null)
            {
                return Results.BadRequest(new { Error = "GitHub token is required." });
            }

            user = new User
            {
                TelegramId = telegramId,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow,
            };
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            var firstRepository = new Repository
            {
                TelegramUserId = telegramId,
                DisplayName = $"{request.RepositoryOwner}/{request.RepositoryName}",
                RepositoryOwner = request.RepositoryOwner,
                RepositoryName = request.RepositoryName,
                GitHubToken = encryptedToken,
                InboxPath = NormalizePath(request.InboxPath) ?? "inbox",
                AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? "inbox/attachments",
                CreatedAt = DateTime.UtcNow,
            };
            dbContext.Repositories.Add(firstRepository);
            await dbContext.SaveChangesAsync();

            user.SelectedRepositoryId = firstRepository.Id;
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { Message = "User registered successfully." });
        }

        // Existing user: settings updates go to the active repository; a user
        // without any repository gets one created from this request.
        var repository = await ResolveRepositoryRowAsync(dbContext, user);
        if (repository == null)
        {
            if (encryptedToken == null)
            {
                return Results.BadRequest(new { Error = "GitHub token is required." });
            }

            repository = new Repository
            {
                TelegramUserId = telegramId,
                DisplayName = $"{request.RepositoryOwner}/{request.RepositoryName}",
                RepositoryOwner = request.RepositoryOwner,
                RepositoryName = request.RepositoryName,
                GitHubToken = encryptedToken,
                InboxPath = NormalizePath(request.InboxPath) ?? "inbox",
                AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? "inbox/attachments",
                CreatedAt = DateTime.UtcNow,
            };
            dbContext.Repositories.Add(repository);
            await dbContext.SaveChangesAsync();

            user.SelectedRepositoryId = repository.Id;
        }
        else
        {
            repository.RepositoryOwner = request.RepositoryOwner;
            repository.RepositoryName = request.RepositoryName;
            repository.InboxPath = NormalizePath(request.InboxPath) ?? repository.InboxPath;
            repository.AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? repository.AttachmentsPath;
            if (encryptedToken != null)
            {
                repository.GitHubToken = encryptedToken;
            }
        }

        user.LastActivityAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        return Results.Ok(new { Message = "User registration details updated successfully." });
    }

    // The user's active repository (explicit selection, else the first one).
    private static async Task<Repository?> ResolveRepositoryRowAsync(AppDbContext dbContext, User user)
    {
        var repositories = await dbContext.Repositories
            .Where(r => r.TelegramUserId == user.TelegramId)
            .OrderBy(r => r.Id)
            .ToListAsync();

        if (repositories.Count == 0)
        {
            return null;
        }

        return repositories.FirstOrDefault(r => r.Id == user.SelectedRepositoryId) ?? repositories[0];
    }

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
