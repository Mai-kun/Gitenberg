using System.Globalization;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Search;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes")
                       .WithTags("Search")
                       .RequireTelegramAuth();

        group.MapGet("/search", SearchNotes)
             .WithName("SearchNotes")
             .WithSummary("Full-text search across the user's indexed notes");
    }

    public static async Task<IResult> SearchNotes(
        [FromQuery] string? query,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        NoteIndexer indexer,
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

        if (string.IsNullOrWhiteSpace(query))
        {
            return Results.BadRequest(
                new { Error = "Search query is required. Provide it in 'query' query parameter." }
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
            return Results.Ok(new List<NoteSearchResult>());
        }

        // Refresh the index right before searching: the incremental sync is
        // cheap when nothing changed and guarantees fresh results (new notes
        // from external sources included).
        try
        {
            await indexer.SynchronizeUserByIdAsync(telegramId.Value, repository.RepositoryId);
        }
        catch (Exception ex)
        {
            // A failed refresh must not break the search — query the current index.
            Console.Error.WriteLine($"Search index refresh failed for user {telegramId}: {ex.Message}");
        }

        var matchExpression = FtsQueryBuilder.Build(query);
        if (string.IsNullOrEmpty(matchExpression))
        {
            return Results.Ok(new List<NoteSearchResult>());
        }

        List<NoteSearchResult> results;
        try
        {
            // TelegramUserId and RepositoryId are stored as TEXT in the FTS
            // table, so they must be bound as strings.
            results = await dbContext.Database.SqlQuery<NoteSearchResult>(
                $"""
                SELECT NotePath,
                       snippet(NoteSearchFts, 3, '<b>', '</b>', '...', 10) AS Snippet
                FROM NoteSearchFts
                WHERE TelegramUserId = {telegramId.Value.ToString(CultureInfo.InvariantCulture)}
                  AND RepositoryId = {repository.RepositoryId.ToString(CultureInfo.InvariantCulture)}
                  AND NoteSearchFts MATCH {matchExpression}
                ORDER BY rank
                LIMIT 50
                """
            ).ToListAsync();
        }
        catch (SqliteException)
        {
            // Raised by FTS5 for malformed MATCH expressions (e.g. an unbalanced quote).
            return Results.BadRequest(new { Error = "Invalid search query syntax." });
        }

        return Results.Ok(results);
    }
}

public record NoteSearchResult(string NotePath, string Snippet);
