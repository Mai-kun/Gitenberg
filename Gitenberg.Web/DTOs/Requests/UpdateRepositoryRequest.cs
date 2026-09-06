namespace Gitenberg.Web.DTOs.Requests;

/// <summary>Token is optional: when omitted the stored token is kept.</summary>
public record UpdateRepositoryRequest(
    string? DisplayName,
    string? GitHubToken,
    string RepositoryOwner,
    string RepositoryName,
    string? InboxPath = null,
    string? AttachmentsPath = null
);
