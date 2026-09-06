using Gitenberg.Web.Models;
using Octokit;

namespace Gitenberg.Web.Services.Abstractions;

public interface IGitHubService
{
    public Task<IReadOnlyList<RepositoryContent>> GetNotesAsync(GitHubRepositoryContext context, string? path = null);
    public Task<string> GetNoteContentAsync(GitHubRepositoryContext context, string path);

    public Task CreateOrUpdateNoteAsync(
        GitHubRepositoryContext context,
        string path,
        string content,
        string commitMessage
    );

    public Task DeleteNoteAsync(GitHubRepositoryContext context, string path, string commitMessage);

    public Task MoveNoteAsync(
        GitHubRepositoryContext context,
        string fromPath,
        string toPath,
        string? content,
        string commitMessage
    );

    public Task UploadBinaryFileAsync(
        GitHubRepositoryContext context,
        string path,
        byte[] contentBytes,
        string commitMessage
    );
}