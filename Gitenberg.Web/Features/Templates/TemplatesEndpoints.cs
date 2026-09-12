using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Sync;
using Gitenberg.Web.Features.TelegramBot.Auth;
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
                       .RequireTelegramAuth();

        group.MapGet("/", ListTemplates)
             .WithName("ListTemplates")
             .WithSummary("List markdown templates from the templates/ folder, with content");
    }

    public static async Task<IResult> ListTemplates(
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
            telegramId.Value,
            repository.RepositoryId,
            TemplatesFolder,
            FetchList
        );

        // Pending local changes win over remote content, so a template edited
        // moments ago is offered in its new form before the sync happens.
        var ops = await pendingSync.GetOpsAsync(telegramId.Value, repository.RepositoryId);

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
