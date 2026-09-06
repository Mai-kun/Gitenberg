namespace Gitenberg.Web.Models;

/// <summary>
/// A single commit that touched a note file, as shown in the version history.
/// </summary>
public record NoteCommitInfo(
    string Sha,
    string? AuthorName,
    string? AuthorLogin,
    string? AuthorAvatarUrl,
    DateTimeOffset Date,
    string Message
);
