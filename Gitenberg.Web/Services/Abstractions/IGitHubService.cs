using Gitenberg.Web.Models;
using Octokit;

namespace Gitenberg.Web.Services.Abstractions;

public interface IGitHubService
{
    public Task<IReadOnlyList<RepositoryContent>> GetNotesAsync(GitHubRepositoryContext context);
    public Task<string> GetNoteContentAsync(GitHubRepositoryContext context, string path);

    public Task CreateOrUpdateNoteAsync(
        GitHubRepositoryContext context,
        string path,
        string content,
        string commitMessage
    );

    public Task DeleteNoteAsync(GitHubRepositoryContext context, string path, string commitMessage);
}