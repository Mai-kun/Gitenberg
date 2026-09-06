namespace Gitenberg.Web.DTOs.Requests;

public record RestoreNoteVersionRequest(string Path, string Sha);
