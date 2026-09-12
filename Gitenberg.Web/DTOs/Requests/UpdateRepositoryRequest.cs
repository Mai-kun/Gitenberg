namespace Gitenberg.Web.DTOs.Requests;

public record UpdateRepositoryRequest(
    string? DisplayName,
    string? GitHubToken,
    string RepositoryOwner,
    string RepositoryName,
    string? InboxPath = null,
    string? AttachmentsPath = null
);
