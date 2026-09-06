namespace Gitenberg.Web.DTOs.Requests;

/// <summary>Path of the note or folder to pin/unpin, relative to the repository root.</summary>
public record PinItemRequest(string Path);
