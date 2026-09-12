namespace Gitenberg.Web.Services.Abstractions;

public sealed record GitHubIdentity(long Id, string Login, string? AvatarUrl);

public interface IGitHubIdentityService
{
    // Validates the token against GitHub and returns the account it belongs to.
    // Throws Octokit.AuthorizationException for an invalid or expired token.
    public Task<GitHubIdentity> ValidateTokenAsync(string token);

    // Throws Octokit.NotFoundException when the token cannot see the repository.
    public Task ValidateRepositoryAccessAsync(string token, string owner, string name);
}
