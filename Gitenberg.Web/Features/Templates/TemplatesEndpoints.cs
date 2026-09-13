using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Auth;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Octokit;

namespace Gitenberg.Web.Features.Templates;

public static class TemplatesEndpoints
{
    // Repository folder scanned for note templates ("Создать по шаблону").
    public const string TemplatesFolder = "templates";

    public static void MapTemplatesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/templates")
                       .WithTags("Templates")
                       .RequireAuth();

        group.MapGet("/", ListTemplates)
             .WithName("ListTemplates")
             .WithSummary("List markdown templates from the templates/ folder, with content");
    }

    public static async Task<IResult> ListTemplates(
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
                new { Error = "No authenticated user: sign in with a GitHub token first." });
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

        // A missing templates/ folder is an empty listing, not an error: the
        // folder appears as soon as the first template is saved into it.
        async Task<IReadOnlyList<RepositoryContent>> FetchList(string? path)
        {
            try
            {
                return await gitHubService.GetNotesAsync(repository.Context, path);
            }
            catch (NotFoundException)
            {
                return Array.Empty<RepositoryContent>();
            }
        }

        var (_, entries) = await pendingSync.ApplyListOverlayAsync(
            userId.Value,
            repository.RepositoryId,
            TemplatesFolder,
            FetchList
        );

        // Pending local changes win over remote content, so a template edited
        // moments ago is offered in its new form before the sync happens.
        var ops = await pendingSync.GetOpsAsync(userId.Value, repository.RepositoryId);

        var templates = new List<object>();
        foreach (var entry in entries)
        {
            if (entry.Type != ContentType.File || entry.Path == null
                || !entry.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var overlay = pendingSync.GetContentOverlay(ops, entry.Path);
            if (overlay.Deleted)
            {
                continue;
            }

            var content = overlay.Found && overlay.Content != null
                ? overlay.Content
                : !string.IsNullOrEmpty(entry.Content)
                    ? entry.Content
                    : await gitHubService.GetNoteContentAsync(repository.Context, overlay.FallbackFromPath ?? entry.Path);

            templates.Add(new
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(entry.Path),
                Path = entry.Path,
                Content = content ?? string.Empty,
            });
        }

        return Results.Ok(templates);
    }
}
