namespace Gitenberg.Web.DTOs.Requests;

/// <summary>
/// Moves (renames) a note. When <paramref name="Content"/> is provided it is
/// written to the new location instead of the original file's content
/// (used when the note was edited in the editor and its path was changed).
/// </summary>
public record MoveNoteRequest(string FromPath, string ToPath, string? Content, string? CommitMessage);
