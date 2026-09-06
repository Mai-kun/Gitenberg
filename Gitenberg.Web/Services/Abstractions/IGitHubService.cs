using Gitenberg.Web.Models;
using Octokit;

namespace Gitenberg.Web.Services.Abstractions;

public interface IGitHubService
{
    public Task<IReadOnlyList<RepositoryContent>> GetNotesAsync(GitHubRepositoryContext context, string? path = null);
    public Task<string> GetNoteContentAsync(GitHubRepositoryContext context, string path);

    /// <summary>
    /// Lists the commits that touched the file at the given path (version history).
    /// Newest first, capped at 50 entries.
    /// </summary>
    public Task<IReadOnlyList<NoteCommitInfo>> GetCommitHistoryAsync(GitHubRepositoryContext context, string path);

    /// <summary>
    /// Reads the file content as it was at the given commit. Throws NotFoundException
    /// when the file did not exist at that commit.
    /// </summary>
    public Task<string> GetNoteContentAtCommitAsync(GitHubRepositoryContext context, string path, string sha);

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

    /// <summary>
    /// Downloads a ZIP archive of the entire repository at the given reference
    /// (branch/tag/SHA). Null reference means the default branch.
    /// </summary>
    public Task<byte[]> GetRepositoryArchiveAsync(GitHubRepositoryContext context, string? reference = null);
}