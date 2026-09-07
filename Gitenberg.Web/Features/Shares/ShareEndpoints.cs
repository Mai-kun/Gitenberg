using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.TelegramBot;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace Gitenberg.Web.Features.Shares;

/// <summary>
/// Public share links ("Share Web View"): a note gets a persistent token URL
/// that renders its Markdown read-only for anyone — no GitHub access needed.
/// Management lives under /api/notes/share behind Telegram auth; the token URL
/// itself and its content endpoint are intentionally unauthenticated — the
/// 128-bit random token is the only credential, and it can be revoked.
/// </summary>
public static class ShareEndpoints
{
    public static void MapShareEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes/share")
                       .WithTags("Shares")
                       .RequireTelegramAuth();

        group.MapPost("/", CreateShareLink)
             .WithName("CreateShareLink")
             .WithSummary("Create (or return the existing) public share link of a note");

        group.MapGet("/", GetShareLink)
             .WithName("GetShareLink")
             .WithSummary("Get the active public share link of a note");

        group.MapDelete("/{token}", RevokeShareLink)
             .WithName("RevokeShareLink")
             .WithSummary("Revoke a public share link (its URL stops working)");

        // Public surface: the page and the JSON it renders from.
        app.MapGet("/share/{token}", ServeSharePage)
           .WithName("ServeSharePage")
           .WithSummary("Read-only rendered view of a shared note")
           .RequireRateLimiting("share");

        app.MapGet("/api/share/{token}", GetSharedContent)
           .WithName("GetSharedContent")
           .WithSummary("Public JSON payload of a shared note (title + markdown)")
           .RequireRateLimiting("share");
    }

    public static async Task<IResult> CreateShareLink(
        [FromBody] ShareNoteRequest? request,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        IGitHubService gitHubService,
        PendingSyncService pendingSync,
        ShareLinksService shareLinks,
        ShareConfiguration shareConfig,
        BotConfiguration botConfig,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.Path.Trim('/').Length == 0)
        {
            return Results.BadRequest(new { Error = "'Path' is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(telegramId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        var path = request.Path.Trim('/');
        var content = await ResolveNoteContentAsync(path, repository, pendingSync, gitHubService, telegramId.Value);
        if (content == null)
        {
            return Results.NotFound(new { Error = $"Note at '{path}' does not exist." });
        }

        var link = await shareLinks.CreateOrGetAsync(telegramId.Value, repository.RepositoryId, path);
        var url = ShareLinkUrlBuilder.BuildUrl(shareConfig.PublicBaseUrl, botConfig.HostAddress, link.Token);
        return Results.Ok(new { link.Token, Url = url, link.CreatedAt });
    }

    public static async Task<IResult> GetShareLink(
        [FromQuery] string path,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        ShareLinksService shareLinks,
        ShareConfiguration shareConfig,
        BotConfiguration botConfig,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        if (string.IsNullOrWhiteSpace(path) || path.Trim('/').Length == 0)
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(telegramId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        var link = await shareLinks.GetActiveAsync(telegramId.Value, repository.RepositoryId, path);
        if (link == null)
        {
            return Results.NotFound(new { Error = $"No active share link for '{path.Trim('/')}'." });
        }

        var url = ShareLinkUrlBuilder.BuildUrl(shareConfig.PublicBaseUrl, botConfig.HostAddress, link.Token);
        return Results.Ok(new { link.Token, Url = url, link.CreatedAt });
    }

    public static async Task<IResult> RevokeShareLink(
        [FromRoute] string token,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ShareLinksService shareLinks,
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

        var revoked = await shareLinks.RevokeAsync(telegramId.Value, token);
        if (!revoked)
        {
            return Results.NotFound(new { Error = "Share link not found or already revoked." });
        }

        return Results.Ok(new { Message = "Share link revoked." });
    }

    // Served relative to the web root (wwwroot) — Results.File's string path
    // is resolved by the web-root file provider, so an absolute path must not
    // be passed here.
    public static IResult ServeSharePage() => Results.File("share.html", "text/html");

    public static async Task<IResult> GetSharedContent(
        [FromRoute] string token,
        IRepositoryContextResolver repositoryResolver,
        IGitHubService gitHubService,
        PendingSyncService pendingSync,
        ShareLinksService shareLinks
    )
    {
        var link = await shareLinks.GetByTokenAsync(token);
        if (link == null)
        {
            return Results.NotFound(new { Error = "Share link not found or revoked." });
        }

        var repository = await repositoryResolver.ResolveByIdAsync(link.TelegramUserId, link.RepositoryId);
        if (repository == null)
        {
            return Results.NotFound(new { Error = "Share link not found or revoked." });
        }

        var content = await ResolveNoteContentAsync(link.NotePath, repository, pendingSync, gitHubService, link.TelegramUserId);
        if (content == null)
        {
            return Results.NotFound(new { Error = "The shared note no longer exists." });
        }

        return Results.Ok(new { Title = GetTitle(link.NotePath), Content = content });
    }

    // The content the owner sees in the app: pending local changes override
    // the remote file. Null means the note does not exist (missing on GitHub
    // or locally deleted); other GitHub failures surface as their exceptions.
    private static async Task<string?> ResolveNoteContentAsync(
        string path,
        ResolvedRepository repository,
        PendingSyncService pendingSync,
        IGitHubService gitHubService,
        long telegramId
    )
    {
        var ops = await pendingSync.GetOpsAsync(telegramId, repository.RepositoryId);
        var overlay = pendingSync.GetContentOverlay(ops, path);
        if (overlay.Deleted)
        {
            return null;
        }
        if (overlay.Found && overlay.Content != null)
        {
            return overlay.Content;
        }

        var sourcePath = overlay.FallbackFromPath ?? path;
        try
        {
            return await gitHubService.GetNoteContentAsync(repository.Context, sourcePath);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private static string GetTitle(string notePath)
    {
        var fileName = notePath.Trim('/').Split('/').Last();
        return Path.GetFileNameWithoutExtension(fileName);
    }
}
