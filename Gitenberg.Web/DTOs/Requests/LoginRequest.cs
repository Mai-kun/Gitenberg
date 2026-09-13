namespace Gitenberg.Web.DTOs.Requests;

public class LoginRequest
{
    public string GitHubToken { get; set; } = string.Empty;

    // Optional: provided on first sign-in or to (re)bind a repository. An
    // existing user signing in without them keeps the active repository.
    public string? RepositoryOwner { get; set; }

    public string? RepositoryName { get; set; }

    public string? InboxPath { get; set; }

    public string? AttachmentsPath { get; set; }
}
