namespace Gitenberg.Web.DTOs.Requests;

public record MoveNoteRequest(string FromPath, string ToPath, string? Content, string? CommitMessage);
