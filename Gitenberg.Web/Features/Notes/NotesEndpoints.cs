using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Activity;
using Gitenberg.Web.Features.Pins;
using Gitenberg.Web.Features.Shares;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.Auth;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Octokit;

namespace Gitenberg.Web.Features.Notes;

public static class NotesEndpoints
{
    private static string GetCacheKey(long userId, int repositoryId, string? path)
    {
        return $"notes_{userId}_{repositoryId}_{path ?? string.Empty}";
    }

    private static string GetCtsKey(long userId, int repositoryId)
    {
        return $"notes_cts_{userId}_{repositoryId}";
    }

    public static void MapNotesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes")
                       .WithTags("Notes")
                       .RequireAuth();

        group.MapGet("/", GetNotes)
             .WithName("GetNotes")
             .WithSummary("List all notes (files) in the active repository");

        group.MapGet("/content", GetNoteContent)
             .WithName("GetNoteContent")
             .WithSummary("Get the text content of a note");

        group.MapPost("/", CreateOrUpdateNote)
             .WithName("CreateOrUpdateNote")
             .WithSummary("Create or update a note");

        group.MapPost("/move", MoveNote)
             .WithName("MoveNote")
             .WithSummary("Move (rename) a note to a new path");

        group.MapDelete("/", DeleteNote)
             .WithName("DeleteNote")
             .WithSummary("Delete a note");
    }

    public static async Task<IResult> GetNotes(
        [FromQuery] string? path,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        IGitHubService gitHubService,
        IMemoryCache memoryCache,
        PendingSyncService pendingSync,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "No authenticated user: sign in with a GitHub token first.",
                }
            );
        }

        return await GetNotesCachedAsync(userId.Value, path, dbContext, repositoryResolver, gitHubService, memoryCache, pendingSync);
    }

    public static async Task<IResult> GetNoteContent(
        [FromQuery] string path,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        IGitHubService gitHubService,
        PendingSyncService pendingSync,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "No authenticated user: sign in with a GitHub token first.",
                }
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        // Pending local changes override the remote file: a queued save wins,
        // a queued delete/move makes the path read as missing.
        var ops = await pendingSync.GetOpsAsync(userId.Value, repository.RepositoryId);
        var overlay = pendingSync.GetContentOverlay(ops, path);
        if (overlay.Deleted)
        {
            return Results.NotFound(new { Error = $"Note at '{path}' does not exist (pending local change)." });
        }
        if (overlay.Found && overlay.Content != null)
        {
            return Results.Ok(new { Path = path, Content = overlay.Content });
        }

        var sourcePath = overlay.FallbackFromPath ?? path;
        var content = await gitHubService.GetNoteContentAsync(repository.Context, sourcePath);
        return Results.Ok(new { Path = path, Content = content });
    }

    public static async Task<IResult> CreateOrUpdateNote(
        [FromBody] CreateOrUpdateNoteRequest? request,
        [FromHeader(Name = "X-Timezone-Offset")] int? timezoneOffset,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PendingSyncService pendingSync,
        IMemoryCache memoryCache,
        ActivityService activityService,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "No authenticated user: sign in with a GitHub token first.",
                }
            );
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return Results.BadRequest(new { Error = "Invalid request body or missing 'Path'." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        // Local-first: the change is queued and pushed to GitHub by the sync
        // timer or the manual flush button.
        await pendingSync.EnqueueAsync(userId.Value, repository.RepositoryId, "save", request.Path, request.Path, request.Content ?? string.Empty, request.CommitMessage);
        BustUserCache(memoryCache, userId.Value, repository.RepositoryId);
        await activityService.RecordAsync(userId.Value, timezoneOffset);

        return Results.Ok(new { Message = $"Note at '{request.Path}' saved locally; it will be synced to GitHub.", Pending = true });
    }

    public static async Task<IResult> DeleteNote(
        [FromQuery] string path,
        [FromQuery] string? commitMessage,
        [FromHeader(Name = "X-Timezone-Offset")] int? timezoneOffset,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PendingSyncService pendingSync,
        IMemoryCache memoryCache,
        ActivityService activityService,
        PinsService? pinsService = null,
        ShareLinksService? shareLinksService = null,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "No authenticated user: sign in with a GitHub token first.",
                }
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        await pendingSync.EnqueueAsync(userId.Value, repository.RepositoryId, "delete", path);
        BustUserCache(memoryCache, userId.Value, repository.RepositoryId);
        if (pinsService != null)
        {
            // Deleting a folder also unpins everything pinned below it.
            await pinsService.RemoveForPathAsync(userId.Value, repository.RepositoryId, path);
        }
        if (shareLinksService != null)
        {
            // Deleted notes and folders lose their public share links.
            await shareLinksService.RemoveForPathAsync(userId.Value, repository.RepositoryId, path);
        }
        await activityService.RecordAsync(userId.Value, timezoneOffset);

        return Results.Ok(new { Message = $"Note at '{path}' deleted locally; it will be synced to GitHub.", Pending = true });
    }

    public static async Task<IResult> MoveNote(
        [FromBody] MoveNoteRequest? request,
        [FromHeader(Name = "X-Timezone-Offset")] int? timezoneOffset,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PendingSyncService pendingSync,
        IMemoryCache memoryCache,
        ActivityService activityService,
        PinsService? pinsService = null,
        ShareLinksService? shareLinksService = null,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "No authenticated user: sign in with a GitHub token first.",
                }
            );
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.FromPath)
            || string.IsNullOrWhiteSpace(request.ToPath))
        {
            return Results.BadRequest(new { Error = "Both 'FromPath' and 'ToPath' are required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        // Local validation of obvious errors (paths, folder-into-itself).
        if (!string.Equals(request.FromPath.Trim('/'), request.ToPath.Trim('/'), StringComparison.OrdinalIgnoreCase)
            && request.ToPath.Trim('/').StartsWith(request.FromPath.Trim('/') + "/", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { Error = $"Cannot move '{request.FromPath}' inside itself." });
        }

        await pendingSync.EnqueueAsync(userId.Value, repository.RepositoryId, "move", request.FromPath, request.ToPath, request.Content);
        BustUserCache(memoryCache, userId.Value, repository.RepositoryId);
        if (pinsService != null)
        {
            // Pins are path metadata independent of content: the moved item and
            // everything pinned below it follow the new path either way.
            await pinsService.ReassignOnMoveAsync(userId.Value, repository.RepositoryId, request.FromPath, request.ToPath);
        }
        if (shareLinksService != null)
        {
            // Share links follow the moved item too, so public URLs survive renames.
            await shareLinksService.ReassignOnMoveAsync(userId.Value, repository.RepositoryId, request.FromPath, request.ToPath);
        }
        await activityService.RecordAsync(userId.Value, timezoneOffset);

        return Results.Ok(new { Message = $"Move of '{request.FromPath}' to '{request.ToPath}' queued; it will be synced to GitHub.", Pending = true });
    }


    internal static void BustUserCache(IMemoryCache memoryCache, long userId, int repositoryId)
    {
        var ctsKey = GetCtsKey(userId, repositoryId);
        if (memoryCache.TryGetValue(ctsKey, out CancellationTokenSource? cts))
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        memoryCache.Remove(ctsKey);
    }

    public static async Task<IResult> GetNotesCachedAsync(
        long userId,
        string? path,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        IGitHubService gitHubService,
        IMemoryCache memoryCache,
        PendingSyncService pendingSync
    )
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        var cacheKey = GetCacheKey(userId, repository.RepositoryId, path);
        var ctsKey = GetCtsKey(userId, repository.RepositoryId);

        var cts = memoryCache.GetOrCreate(ctsKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
            return new CancellationTokenSource();
        });

        if (memoryCache.TryGetValue(cacheKey, out List<object>? cachedNotes) && cachedNotes != null)
        {
            return Results.Ok(cachedNotes);
        }

        // Pending local changes are overlaid on the remote listing, and the
        // listing itself may need to be read from the pre-move folder path.
        var (effectivePath, contents) = await pendingSync.ApplyListOverlayAsync(
            userId,
            repository.RepositoryId,
            path,
            async (p) => await gitHubService.GetNotesAsync(repository.Context, p)
        );

        var notes = contents.Select(c => (object)new
        {
            c.Name,
            c.Path,
            c.Sha,
            c.Size,
            Type = c.Type.ToString(),
            c.DownloadUrl,
            c.HtmlUrl,
        }
        ).ToList();

        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(30));

        if (cts != null)
        {
            cacheEntryOptions.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(cts.Token));
        }

        memoryCache.Set(cacheKey, notes, cacheEntryOptions);

        return Results.Ok(notes);
    }
}
