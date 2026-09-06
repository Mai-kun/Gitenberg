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

/// <summary>
/// Version history for notes: every edit is a commit, so history is the commit
/// list of the file and restoring a version enqueues a "save" op with the old
/// content (a new commit, no Git history rewrite).
/// </summary>
public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes")
                       .WithTags("Notes")
                       .RequireTelegramAuth();

        group.MapGet("/history", GetNoteHistory)
             .WithName("GetNoteHistory")
             .WithSummary("List commits that changed a note (version history)");

        group.MapGet("/history/content", GetNoteVersionContent)
             .WithName("GetNoteVersionContent")
             .WithSummary("Get the content of a note as it was at a given commit");

        group.MapPost("/history/restore", RestoreNoteVersion)
             .WithName("RestoreNoteVersion")
             .WithSummary("Queue a save op restoring a note to its content at a given commit");
    }

    public static async Task<IResult> GetNoteHistory(
        [FromQuery] string path,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
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

        var repository = await repositoryResolver.ResolveActiveAsync(telegramId.Value);
        if (repository == null)
        {
            return Results.BadRequest(new { Error = "GitHub repository is not configured for this user." });
        }

        IReadOnlyList<NoteCommitInfo> commits;
        try
        {
            commits = await gitHubService.GetCommitHistoryAsync(repository.Context, path);
        }
        catch (NotFoundException)
        {
            return Results.NotFound(new { Error = $"Note at '{path}' does not exist." });
        }

        // Remote history does not include unsynced local changes; the UI shows
        // a warning instead of silently hiding them.
        var ops = await pendingSync.GetOpsAsync(telegramId.Value, repository.RepositoryId);
        var overlay = pendingSync.GetContentOverlay(ops, path.Trim('/'));
        var hasPendingChanges = overlay.Found || overlay.Deleted;

        return Results.Ok(new { Commits = commits, HasPendingChanges = hasPendingChanges });
    }

    public static async Task<IResult> GetNoteVersionContent(
        [FromQuery] string path,
        [FromQuery] string sha,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        IGitHubService gitHubService,
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

        if (string.IsNullOrWhiteSpace(sha))
        {
            return Results.BadRequest(new { Error = "Sha parameter is required." });
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

        string content;
        try
        {
            content = await gitHubService.GetNoteContentAtCommitAsync(repository.Context, path, sha);
        }
        catch (NotFoundException)
        {
            return Results.NotFound(
                new { Error = $"File '{path}' not found at commit '{sha}'." }
            );
        }

        return Results.Ok(new { Path = path, Sha = sha, Content = content });
    }

    public static async Task<IResult> RestoreNoteVersion(
        [FromBody] RestoreNoteVersionRequest? request,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        IGitHubService gitHubService,
        PendingSyncService pendingSync,
        IMemoryCache memoryCache,
        Reminders.ReminderService reminderService,
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
            || string.IsNullOrWhiteSpace(request.Path)
            || string.IsNullOrWhiteSpace(request.Sha))
        {
            return Results.BadRequest(new { Error = "Both 'Path' and 'Sha' are required." });
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

        string content;
        try
        {
            content = await gitHubService.GetNoteContentAtCommitAsync(repository.Context, request.Path, request.Sha);
        }
        catch (NotFoundException)
        {
            return Results.NotFound(
                new { Error = $"File '{request.Path}' not found at commit '{request.Sha}'." }
            );
        }

        // A string cannot use a numeric format specifier like {sha:7} — slice it.
        var shortSha = request.Sha.Length >= 7 ? request.Sha[..7] : request.Sha;
        var cleanPath = request.Path.Trim('/');
        var commitMessage = $"Restore note: {cleanPath} (← {shortSha})";
        // Local-first: restoring is a regular save op, pushed to GitHub by sync.
        await pendingSync.EnqueueAsync(telegramId.Value, repository.RepositoryId, "save", request.Path, null, content, commitMessage);
        NotesEndpoints.BustUserCache(memoryCache, telegramId.Value, repository.RepositoryId);
        // Restored content may add or remove "@remind" markers — reconcile.
        await reminderService.UpsertForNoteAsync(telegramId.Value, repository.RepositoryId, request.Path, content);

        return Results.Ok(
            new
            {
                Message = $"Note at '{request.Path}' will be restored to commit {shortSha}; it will be synced to GitHub.",
                Pending = true,
            }
        );
    }
}
