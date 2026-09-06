using System.Globalization;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Tasks;

public static class TasksEndpoints
{
    private const int MaxTasks = 500;

    public static void MapTasksEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes")
                       .WithTags("Tasks")
                       .RequireTelegramAuth();

        group.MapGet("/tasks", GetTasks)
             .WithName("GetNoteTasks")
             .WithSummary("All markdown checkboxes across the user's notes (pending local changes applied)");
    }

    public static async Task<IResult> GetTasks(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        NoteIndexer indexer,
        PendingSyncService pendingSync,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." }
            );
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(telegramId.Value);
        if (repository == null)
        {
            return Results.Ok(new List<NoteTask>());
        }

        // Same freshness contract as search: refresh the active repository's
        // index first (cheap when nothing changed), then read from it.
        try
        {
            await indexer.SynchronizeUserByIdAsync(telegramId.Value, repository.RepositoryId);
        }
        catch
        {
            // Index freshness is best-effort.
        }

        var uid = telegramId.Value.ToString(CultureInfo.InvariantCulture);
        var rid = repository.RepositoryId.ToString(CultureInfo.InvariantCulture);
        var rows = await dbContext.Database.SqlQuery<FtsContentRow>(
            $"SELECT NotePath AS NotePath, Content AS Content FROM NoteSearchFts WHERE TelegramUserId = {uid} AND RepositoryId = {rid}"
        ).ToListAsync();

        // Pending local changes win over the indexed remote content: a queued
        // save replaces the content, a delete/move removes the note (both
        // paths for a move without payload — its content is unknown yet).
        var ops = await pendingSync.GetOpsAsync(telegramId.Value, repository.RepositoryId);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case "save":
                    overrides[op.FromPath] = op.Content ?? string.Empty;
                    break;
                case "delete":
                    excluded.Add(op.FromPath);
                    break;
                case "move":
                    excluded.Add(op.FromPath);
                    if (op.Content != null)
                    {
                        overrides[op.ToPath!] = op.Content;
                    }
                    else
                    {
                        excluded.Add(op.ToPath!);
                    }
                    break;
            }
        }

        var tasks = new List<NoteTask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (excluded.Contains(row.NotePath) || !seen.Add(row.NotePath))
            {
                continue;
            }

            var content = overrides.TryGetValue(row.NotePath, out var pendingContent)
                ? pendingContent
                : StripPathPrefix(row.NotePath, row.Content);
            tasks.AddRange(TaskListBuilder.Parse(row.NotePath, content));
        }

        // Brand-new notes that exist only as pending saves (no indexed row).
        foreach (var (path, content) in overrides)
        {
            if (excluded.Contains(path) || !seen.Add(path))
            {
                continue;
            }
            tasks.AddRange(TaskListBuilder.Parse(path, content));
        }

        return Results.Ok(tasks.Take(MaxTasks).ToList());
    }

    // FTS rows store "path\ncontent"; the note body starts after the first line.
    private static string? StripPathPrefix(string notePath, string? content)
    {
        if (content == null)
        {
            return null;
        }

        var prefix = notePath + "\n";
        return content.StartsWith(prefix, StringComparison.Ordinal) ? content[prefix.Length..] : content;
    }

    private sealed record FtsContentRow(string NotePath, string? Content);
}
