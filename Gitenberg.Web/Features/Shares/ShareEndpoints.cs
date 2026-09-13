using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.Auth;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace Gitenberg.Web.Features.Shares;

public static class ShareEndpoints
{
    public static void MapShareEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes/share")
                       .WithTags("Shares")
                       .RequireAuth();

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
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        IGitHubService gitHubService,
        PendingSyncService pendingSync,
        ShareLinksService shareLinks,
        ShareConfiguration shareConfig,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.Path.Trim('/').Length == 0)
        {
            return Results.BadRequest(new { Error = "'Path' is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        var path = request.Path.Trim('/');
        var content = await ResolveNoteContentAsync(path, repository, pendingSync, gitHubService, userId.Value);
        if (content == null)
        {
            return Results.NotFound(new { Error = $"Note at '{path}' does not exist." });
        }

        var link = await shareLinks.CreateOrGetAsync(userId.Value, repository.RepositoryId, path);
        var url = ShareLinkUrlBuilder.BuildUrl(shareConfig.PublicBaseUrl, RequestBaseUrl(httpContext), link.Token);
        return Results.Ok(new { link.Token, Url = url, link.CreatedAt });
    }

    public static async Task<IResult> GetShareLink(
        [FromQuery] string path,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        ShareLinksService shareLinks,
        ShareConfiguration shareConfig,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." });
        }

        if (string.IsNullOrWhiteSpace(path) || path.Trim('/').Length == 0)
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        var link = await shareLinks.GetActiveAsync(userId.Value, repository.RepositoryId, path);
        if (link == null)
        {
            return Results.NotFound(new { Error = $"No active share link for '{path.Trim('/')}'." });
        }

        var url = ShareLinkUrlBuilder.BuildUrl(shareConfig.PublicBaseUrl, RequestBaseUrl(httpContext), link.Token);
        return Results.Ok(new { link.Token, Url = url, link.CreatedAt });
    }

    // Share URLs anchor to the deployment address. ShareConfiguration:PublicBaseUrl
    // wins when set (reverse proxies often expose a different external host);
    // otherwise the current request's own origin is used.
    private static string RequestBaseUrl(HttpContext? httpContext)
    {
        if (httpContext?.Request == null)
        {
            return string.Empty;
        }

        return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}";
    }

    public static async Task<IResult> RevokeShareLink(
        [FromRoute] string token,
        AppDbContext dbContext,
        ShareLinksService shareLinks,
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

        var revoked = await shareLinks.RevokeAsync(userId.Value, token);
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
        long userId
    )
    {
        var ops = await pendingSync.GetOpsAsync(userId, repository.RepositoryId);
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
