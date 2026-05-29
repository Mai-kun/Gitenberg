namespace Gitenberg.Web.DTOs.Requests;

public record CreateOrUpdateNoteRequest(string Path, string Content, string? CommitMessage);