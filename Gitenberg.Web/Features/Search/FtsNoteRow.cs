namespace Gitenberg.Web.Features.Search;

// A content row read back from NoteSearchFts. The indexer stores
// "path\ncontent" in the Content column, so the note body starts after
// the first line.
public sealed record FtsNoteRow(string NotePath, string? Content)
{
    public string? Body()
    {
        if (Content == null)
        {
            return null;
        }

        var prefix = NotePath + "\n";
        return Content.StartsWith(prefix, StringComparison.Ordinal) ? Content[prefix.Length..] : Content;
    }
}
