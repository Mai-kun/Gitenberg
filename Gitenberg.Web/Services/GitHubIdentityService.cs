using Gitenberg.Web.Services.Abstractions;
using Octokit;

namespace Gitenberg.Web.Services;

public class GitHubIdentityService : IGitHubIdentityService
{
    // Test seam: when set, this client is used instead of creating one per call.
    private readonly IGitHubClient? _clientOverride;

    public GitHubIdentityService()
    {
    }

    internal GitHubIdentityService(IGitHubClient clientOverride)
    {
        _clientOverride = clientOverride;
    }

    public async Task<GitHubIdentity> ValidateTokenAsync(string token)
    {
        var client = ResolveClient(token);
        var user = await client.User.Current();
        return new GitHubIdentity(user.Id, user.Login, user.AvatarUrl);
    }

    public async Task ValidateRepositoryAccessAsync(string token, string owner, string name)
    {
        var client = ResolveClient(token);
        await client.Repository.Get(owner, name);
    }

    private IGitHubClient ResolveClient(string token) => _clientOverride ?? CreateClient(token);

    private static GitHubClient CreateClient(string token)
    {
        return new GitHubClient(new ProductHeaderValue("Gitenberg"))
        {
            Credentials = new Credentials(token),
        };
    }
}
