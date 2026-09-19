using System.Globalization;

namespace Gitenberg.Web.Features.Graph;

public static class GraphEndpoints
{
    private const int MaxNodes = 1000;

    public static void MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes").WithTags("Graph").RequireAuth();

        group
            .MapGet("/graph", GetGraph)
            .WithName("GetNotesGraph")
            .WithSummary(
                "All notes of the active repository and their [[WikiLink]] connections as a link graph"
            );
    }

    public static async Task<IResult> GetGraph(
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
            return Results.Ok(new WikiGraph([], []));
        }

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
        var rows = await dbContext
            .Database.SqlQuery<FtsNoteRow>(
                $"SELECT NotePath AS NotePath, Content AS Content FROM NoteSearchFts WHERE TelegramUserId = {uid} AND RepositoryId = {rid}"
            )
            .ToListAsync();

        var (excluded, overrides) = await pendingSync.GetContentOverlayAsync(
            userId.Value,
            repository.RepositoryId
        );

        var notes = new List<(string Path, string Content)>();
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
            notes.Add((row.NotePath, content ?? string.Empty));
        }

        foreach (var (path, content) in overrides)
        {
            if (excluded.Contains(path) || !seen.Add(path))
            {
                continue;
            }
            notes.Add((path, content));
        }

        var graph = WikiLinkGraph.Build(notes, MaxNodes);
        return Results.Ok(graph);
    }
}
