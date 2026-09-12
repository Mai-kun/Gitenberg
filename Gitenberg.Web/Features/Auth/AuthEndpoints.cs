using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octokit;
using Repository = Gitenberg.Web.Models.Repository;
using User = Gitenberg.Web.Models.User;

namespace Gitenberg.Web.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
                       .WithTags("Auth");

        group.MapPost("/login", Login)
             .WithName("Login")
             .WithSummary("Sign in with a GitHub token, optionally binding a repository")
             .RequireRateLimiting("auth");

        group.MapPost("/logout", Logout)
             .WithName("Logout")
             .WithSummary("Revoke the current session and clear the cookie");

        group.MapGet("/session", SessionStatus)
             .WithName("SessionStatus")
             .WithSummary("Whether the current session is authenticated")
             .RequireAuth();
    }

    // Sign-in doubles as first-time registration: a new GitHub identity gets a
    // user row (keyed by the GitHub user id) and its first repository binding.
    public static async Task<IResult> Login(
        [FromBody] LoginRequest? request,
        AppDbContext dbContext,
        IGitHubIdentityService identityService,
        ITokenEncryptionService encryptionService,
        WebAuthService sessions,
        WebAuthConfiguration config,
        IWebHostEnvironment environment,
        HttpContext httpContext
    )
    {
        if (request == null || string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            return Results.BadRequest(new { Error = "GitHub token is required." });
        }

        var owner = request.RepositoryOwner?.Trim();
        var name = request.RepositoryName?.Trim();
        if (string.IsNullOrWhiteSpace(owner) != string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new { Error = "Provide both repository owner and name, or neither." });
        }

        GitHubIdentity identity;
        try
        {
            identity = await identityService.ValidateTokenAsync(request.GitHubToken);
        }
        catch (AuthorizationException)
        {
            return Results.Json(
                new { Error = "GitHub token is invalid or expired. Create a new one and try again." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var userId = identity.Id;
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);

        if (owner != null && name != null)
        {
            try
            {
                await identityService.ValidateRepositoryAccessAsync(request.GitHubToken, owner, name);
            }
            catch (NotFoundException)
            {
                return Results.BadRequest(new { Error = $"Repository '{owner}/{name}' was not found or the token has no access to it." });
            }

            // First sign-in: the user row must exist before the repository (FK).
            if (user == null)
            {
                user = new User
                {
                    TelegramId = userId,
                    CreatedAt = DateTime.UtcNow,
                    LastActivityAt = DateTime.UtcNow,
                };
                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync();
            }

            var repositories = await dbContext.Repositories
                .Where(r => r.TelegramUserId == userId)
                .ToListAsync();
            // Owner/repo names are matched case-insensitively in memory, so
            // re-typing the same repository with different casing rebinds it
            // instead of creating a duplicate row.
            var repository = repositories.FirstOrDefault(r =>
                string.Equals(r.RepositoryOwner, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.RepositoryName, name, StringComparison.OrdinalIgnoreCase));
            var encryptedToken = encryptionService.EncryptToken(request.GitHubToken, TimeSpan.FromDays(365));
            if (repository == null)
            {
                repository = new Repository
                {
                    TelegramUserId = userId,
                    DisplayName = $"{owner}/{name}",
                    RepositoryOwner = owner,
                    RepositoryName = name,
                    GitHubToken = encryptedToken,
                    InboxPath = NormalizePath(request.InboxPath) ?? "inbox",
                    AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? "inbox/attachments",
                    CreatedAt = DateTime.UtcNow,
                };
                dbContext.Repositories.Add(repository);
            }
            else
            {
                repository.GitHubToken = encryptedToken;
                repository.InboxPath = NormalizePath(request.InboxPath) ?? repository.InboxPath;
                repository.AttachmentsPath = NormalizePath(request.AttachmentsPath) ?? repository.AttachmentsPath;
            }

            await dbContext.SaveChangesAsync();
            user.SelectedRepositoryId = repository.Id;
        }
        else if (user == null || !await dbContext.Repositories.AnyAsync(r => r.TelegramUserId == userId))
        {
            // Known identity without any bound repository cannot sign in blind.
            return Results.BadRequest(new { Error = "Repository owner and name are required on first sign-in." });
        }

        user.LastActivityAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        var token = await sessions.CreateSessionAsync(userId, TimeSpan.FromDays(config.SessionLifetimeDays));
        httpContext.Response.Cookies.Append(config.CookieName, token, BuildCookieOptions(config, environment));

        return Results.Ok(new { Login = identity.Login, AvatarUrl = identity.AvatarUrl });
    }

    public static async Task<IResult> Logout(
        WebAuthService sessions,
        WebAuthConfiguration config,
        IWebHostEnvironment environment,
        HttpContext httpContext
    )
    {
        await sessions.DeleteSessionAsync(httpContext.Request.Cookies[config.CookieName]);
        httpContext.Response.Cookies.Delete(config.CookieName, BuildCookieOptions(config, environment));
        return Results.Ok(new { Message = "Signed out." });
    }

    public static IResult SessionStatus()
    {
        // Reaching this handler means WebAuthFilter resolved the session.
        return Results.Ok(new { Authenticated = true });
    }

    private static CookieOptions BuildCookieOptions(WebAuthConfiguration config, IWebHostEnvironment environment) => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(config.SessionLifetimeDays),
        IsEssential = true,
        Path = "/",
    };

    // Optional folder settings are trimmed of slashes/whitespace; an empty
    // result keeps the default value.
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
