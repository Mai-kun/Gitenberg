namespace Gitenberg.Web.Features.Search;

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
