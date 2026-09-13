using System.Globalization;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.Auth;
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
                       .RequireAuth();

        group.MapGet("/tasks", GetTasks)
             .WithName("GetNoteTasks")
             .WithSummary("All markdown checkboxes across the user's notes (pending local changes applied)");
    }

    public static async Task<IResult> GetTasks(
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        NoteIndexer indexer,
        PendingSyncService pendingSync,
        HttpContext? httpContext = null
    )
    {
        var userId = CurrentUserId.From(httpContext);
        if (userId == null)
        {
            return Results.BadRequest(
                new { Error = "No authenticated user: sign in with a GitHub token first." }
            );
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with ID {userId} not found." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(userId.Value);
        if (repository == null)
        {
            return Results.Ok(new List<NoteTask>());
        }

        // Same freshness contract as search: refresh the active repository's
        // index first (cheap when nothing changed), then read from it.
        try
        {
            await indexer.SynchronizeUserByIdAsync(userId.Value, repository.RepositoryId);
        }
        catch
        {
            // Index freshness is best-effort.
        }

        var uid = userId.Value.ToString(CultureInfo.InvariantCulture);
        var rid = repository.RepositoryId.ToString(CultureInfo.InvariantCulture);
        var rows = await dbContext.Database.SqlQuery<FtsNoteRow>(
            $"SELECT NotePath AS NotePath, Content AS Content FROM NoteSearchFts WHERE TelegramUserId = {uid} AND RepositoryId = {rid}"
        ).ToListAsync();

        // Pending local changes win over the indexed remote content.
        var (excluded, overrides) = await pendingSync.GetContentOverlayAsync(userId.Value, repository.RepositoryId);

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
                : row.Body();
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
}
