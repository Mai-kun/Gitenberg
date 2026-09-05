using Gitenberg.Web.Database;
using Gitenberg.Web.DTOs.Requests;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Octokit;

namespace Gitenberg.Web.Features.Notes;

public static class NotesEndpoints
{
    private static string GetCacheKey(long telegramId, string? path)
    {
        return $"notes_{telegramId}_{path ?? string.Empty}";
    }

    public static void MapNotesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes")
                       .WithTags("Notes")
                       .RequireTelegramAuth();

        group.MapGet("/", GetNotes)
             .WithName("GetNotes")
             .WithSummary("List all notes (files) in the repository");

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
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        IGitHubService gitHubService,
        IMemoryCache memoryCache,
        PendingSyncService pendingSync,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
        }

        return await GetNotesCachedAsync(telegramId.Value, path, dbContext, encryptionService, gitHubService, memoryCache, pendingSync);
    }

    public static async Task<IResult> GetNoteContent(
        [FromQuery] string path,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        IGitHubService gitHubService,
        PendingSyncService pendingSync,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        if (string.IsNullOrWhiteSpace(user.GitHubToken))
        {
            return Results.BadRequest(new { Error = "GitHub token is not configured for this user." });
        }

        var decryptedToken = encryptionService.DecryptToken(user.GitHubToken);
        var context = new GitHubRepositoryContext(decryptedToken, user.RepositoryOwner, user.RepositoryName);

        // Pending local changes override the remote file: a queued save wins,
        // a queued delete/move makes the path read as missing.
        var ops = await pendingSync.GetOpsAsync(telegramId.Value);
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
        var content = await gitHubService.GetNoteContentAsync(context, sourcePath);
        return Results.Ok(new { Path = path, Content = content });
    }

    public static async Task<IResult> CreateOrUpdateNote(
        [FromBody] CreateOrUpdateNoteRequest? request,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        PendingSyncService pendingSync,
        IMemoryCache memoryCache,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Path))
        {
            return Results.BadRequest(new { Error = "Invalid request body or missing 'Path'." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        // Local-first: the change is queued and pushed to GitHub by the sync
        // timer or the manual flush button.
        var commitMessage = string.IsNullOrWhiteSpace(request.CommitMessage)
            ? $"Update note: {request.Path}"
            : request.CommitMessage;

        await pendingSync.EnqueueAsync(telegramId.Value, "save", request.Path, request.Path, request.Content ?? string.Empty);
        BustUserCache(memoryCache, telegramId.Value);

        return Results.Ok(new { Message = $"Note at '{request.Path}' saved locally; it will be synced to GitHub.", Pending = true });
    }

    public static async Task<IResult> DeleteNote(
        [FromQuery] string path,
        [FromQuery] string? commitMessage,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        PendingSyncService pendingSync,
        IMemoryCache memoryCache,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.BadRequest(new { Error = "Path parameter is required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var commit = string.IsNullOrWhiteSpace(commitMessage)
            ? $"Delete note: {path}"
            : commitMessage;

        await pendingSync.EnqueueAsync(telegramId.Value, "delete", path);
        BustUserCache(memoryCache, telegramId.Value);

        return Results.Ok(new { Message = $"Note at '{path}' deleted locally; it will be synced to GitHub.", Pending = true });
    }

    public static async Task<IResult> MoveNote(
        [FromBody] MoveNoteRequest? request,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        PendingSyncService pendingSync,
        IMemoryCache memoryCache,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new
                {
                    Error =
                        "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter.",
                }
            );
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.FromPath)
            || string.IsNullOrWhiteSpace(request.ToPath))
        {
            return Results.BadRequest(new { Error = "Both 'FromPath' and 'ToPath' are required." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        // Local validation of obvious errors (paths, folder-into-itself).
        if (!string.Equals(request.FromPath.Trim('/'), request.ToPath.Trim('/'), StringComparison.OrdinalIgnoreCase)
            && request.ToPath.Trim('/').StartsWith(request.FromPath.Trim('/') + "/", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { Error = $"Cannot move '{request.FromPath}' inside itself." });
        }

        await pendingSync.EnqueueAsync(telegramId.Value, "move", request.FromPath, request.ToPath, request.Content);
        BustUserCache(memoryCache, telegramId.Value);

        return Results.Ok(new { Message = $"Move of '{request.FromPath}' to '{request.ToPath}' queued; it will be synced to GitHub.", Pending = true });
    }

    private static void BustUserCache(IMemoryCache memoryCache, long telegramId)
    {
        var ctsKey = $"notes_cts_{telegramId}";
        if (memoryCache.TryGetValue(ctsKey, out CancellationTokenSource? cts))
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        memoryCache.Remove(ctsKey);
    }

    public static async Task<IResult> GetNotesCachedAsync(
        long telegramId,
        string? path,
        AppDbContext dbContext,
        ITokenEncryptionService encryptionService,
        IGitHubService gitHubService,
        IMemoryCache memoryCache,
        PendingSyncService pendingSync
    )
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        if (string.IsNullOrWhiteSpace(user.GitHubToken))
        {
            return Results.BadRequest(new { Error = "GitHub token is not configured for this user." });
        }

        var cacheKey = GetCacheKey(telegramId, path);
        var ctsKey = $"notes_cts_{telegramId}";

        var cts = memoryCache.GetOrCreate(ctsKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
            return new CancellationTokenSource();
        });

        if (memoryCache.TryGetValue(cacheKey, out List<object>? cachedNotes) && cachedNotes != null)
        {
            return Results.Ok(cachedNotes);
        }

        var decryptedToken = encryptionService.DecryptToken(user.GitHubToken);
        var context = new GitHubRepositoryContext(decryptedToken, user.RepositoryOwner, user.RepositoryName);

        // Pending local changes are overlaid on the remote listing, and the
        // listing itself may need to be read from the pre-move folder path.
        var (effectivePath, contents) = await pendingSync.ApplyListOverlayAsync(
            telegramId,
            path,
            async (p) => await gitHubService.GetNotesAsync(context, p)
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
