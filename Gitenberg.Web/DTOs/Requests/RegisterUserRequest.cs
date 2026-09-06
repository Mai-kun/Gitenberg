namespace Gitenberg.Web.DTOs.Requests;

public record RegisterUserRequest(
    long TelegramId,
    string GitHubToken,
    string RepositoryOwner,
    string RepositoryName,
    string? InboxPath = null,
    string? AttachmentsPath = null
);
