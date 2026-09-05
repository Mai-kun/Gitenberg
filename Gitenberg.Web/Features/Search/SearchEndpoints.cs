using System.Globalization;
using Gitenberg.Web.Database;
using Gitenberg.Web.Features.TelegramBot.Auth;
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

        // Refresh the index right before searching: the incremental sync is
        // cheap when nothing changed and guarantees fresh results (new notes
        // from external sources included).
        try
        {
            await indexer.SynchronizeUserByIdAsync(telegramId.Value);
        }
        catch (Exception ex)
        {
            // A failed refresh must not break the search — query the current index.
            Console.Error.WriteLine($"Search index refresh failed for user {telegramId}: {ex.Message}");
        }

        // Build a safe FTS5 MATCH expression: each whitespace-separated term
        // becomes a quoted prefix term ("term"*). This avoids syntax errors
        // from user input (quotes, #tags, punctuation) and enables partial
        // matches for titles and tags.
        var matchExpression = BuildMatchExpression(query);
        if (string.IsNullOrEmpty(matchExpression))
        {
            return Results.Ok(new List<NoteSearchResult>());
        }

        List<NoteSearchResult> results;
        try
        {
            // TelegramUserId is stored as TEXT in the FTS table, so it must be bound as a string.
            results = await dbContext.Database.SqlQuery<NoteSearchResult>(
                $"""
                SELECT NotePath,
                       snippet(NoteSearchFts, 2, '<b>', '</b>', '...', 10) AS Snippet
                FROM NoteSearchFts
                WHERE TelegramUserId = {telegramId.Value.ToString(CultureInfo.InvariantCulture)}
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

    private static string BuildMatchExpression(string rawQuery)
    {
        var terms = rawQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Replace("\"", string.Empty).Trim())
            .Where(term => term.Length > 0)
            .Select(term => $"\"{term}\"*")
            .Take(8)
            .ToList();
        return string.Join(' ', terms);
    }
}

public record NoteSearchResult(string NotePath, string Snippet);
