namespace Gitenberg.Web.Models;

public record NoteCommitInfo(
    string Sha,
    string? AuthorName,
    string? AuthorLogin,
    string? AuthorAvatarUrl,
    DateTimeOffset Date,
    string Message
);
