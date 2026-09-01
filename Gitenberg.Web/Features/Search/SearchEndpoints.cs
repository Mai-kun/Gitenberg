using System.Globalization;
using Gitenberg.Web.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Features.Search;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notes")
                       .WithTags("Search");

        group.MapGet("/search", SearchNotes)
             .WithName("SearchNotes")
             .WithSummary("Full-text search across the user's indexed notes");
    }

    public static async Task<IResult> SearchNotes(
        [FromQuery] string? query,
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext
    )
    {
        var telegramId = headerTelegramId ?? queryTelegramId;
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
                  AND NoteSearchFts MATCH {query}
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
