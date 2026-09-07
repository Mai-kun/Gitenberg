namespace Gitenberg.Web.DTOs.Requests;

/// <summary>Path of the note to create a public share link for, relative to the repository root.</summary>
public record ShareNoteRequest(string Path);
