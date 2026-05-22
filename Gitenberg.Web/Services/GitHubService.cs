using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Octokit;

namespace Gitenberg.Web.Services;

public class GitHubService : IGitHubService
{
    public Task<IReadOnlyList<RepositoryContent>> GetNotesAsync(GitHubRepositoryContext context)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetNoteContentAsync(GitHubRepositoryContext context, string path)
    {
        throw new NotImplementedException();
    }

    public Task DeleteNoteAsync(GitHubRepositoryContext context, string path, string commitMessage)
    {
        throw new NotImplementedException();
    }

    public Task CreateOrUpdateNoteAsync(
        GitHubRepositoryContext context,
        string path,
        string content,
        string commitMessage
    )
    {
        throw new NotImplementedException();
    }
}