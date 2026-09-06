namespace Gitenberg.Web.Features.Search;

/// <summary>
/// Builds a safe FTS5 MATCH expression from user input: each whitespace-separated
/// term becomes a quoted prefix term ("term"*). This avoids syntax errors
/// from user input (quotes, #tags, punctuation) and enables partial
/// matches for titles and tags.
/// </summary>
public static class FtsQueryBuilder
{
    public const int MaxTerms = 8;

    public static string Build(string rawQuery)
    {
        var terms = rawQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Replace("\"", string.Empty).Trim())
            // Punctuation-only terms (e.g. "#", "!!!") produce empty FTS5 phrases.
            .Where(term => term.Length > 0 && term.Any(char.IsLetterOrDigit))
            .Select(term => $"\"{term}\"*")
            .Take(MaxTerms)
            .ToList();
        return string.Join(' ', terms);
    }
}
