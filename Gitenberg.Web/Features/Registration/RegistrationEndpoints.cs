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
             .WithSummary("Register or update a user");

        group.MapGet("/", GetSettings)
             .WithName("GetSettings")
             .WithSummary("Current repository settings (token is never returned)");
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

        return Results.Ok(new
        {
            RepositoryOwner = user.RepositoryOwner,
            RepositoryName = user.RepositoryName,
            HasToken = !string.IsNullOrWhiteSpace(user.GitHubToken),
            InboxPath = user.InboxPath,
            AttachmentsPath = user.AttachmentsPath,
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

        // The token is only required on first registration; from the settings
        // screen it can be omitted to keep the stored one.
        if (user == null && string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            return Results.BadRequest(new { Error = "GitHub token is required." });
        }

        string? encryptedToken = null;
        if (!string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            encryptedToken = encryptionService.EncryptToken(request.GitHubToken, TimeSpan.FromDays(365));
        }

        if (user == null)
        {
            user = new User
            {
                TelegramId = telegramId,
                GitHubToken = encryptedToken!,
                RepositoryOwner = request.RepositoryOwner,
                RepositoryName = request.RepositoryName,
                InboxPath = NormalizePath(request.InboxPath) ?? "inbox",
                AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? "inbox/attachments",
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow,
            };
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { Message = "User registered successfully." });
        }

        if (encryptedToken != null)
        {
            user.GitHubToken = encryptedToken;
        }
        user.RepositoryOwner = request.RepositoryOwner;
        user.RepositoryName = request.RepositoryName;
        user.InboxPath = NormalizePath(request.InboxPath) ?? user.InboxPath;
        user.AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? user.AttachmentsPath;
        user.LastActivityAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        return Results.Ok(new { Message = "User registration details updated successfully." });
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