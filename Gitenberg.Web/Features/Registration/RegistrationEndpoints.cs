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

        var encryptedToken = encryptionService.EncryptToken(request.GitHubToken, TimeSpan.FromDays(365));

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            user = new User
            {
                TelegramId = telegramId,
                GitHubToken = encryptedToken,
                RepositoryOwner = request.RepositoryOwner,
                RepositoryName = request.RepositoryName,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow,
            };
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            return Results.Ok(new { Message = "User registered successfully." });
        }

        user.GitHubToken = encryptedToken;
        user.RepositoryOwner = request.RepositoryOwner;
        user.RepositoryName = request.RepositoryName;
        user.LastActivityAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        return Results.Ok(new { Message = "User registration details updated successfully." });
    }
}