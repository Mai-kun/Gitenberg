namespace Gitenberg.Web.DTOs.Requests;

public record CreateRepositoryRequest(
    string DisplayName,
    string GitHubToken,
    string RepositoryOwner,
    string RepositoryName,
    string? InboxPath = null,
    string? AttachmentsPath = null
);
