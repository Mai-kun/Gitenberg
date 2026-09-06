using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Octokit;

namespace Gitenberg.Tests.Mocks;

public class MockGitHubService : IGitHubService
{
    public Func<GitHubRepositoryContext, string?, Task<IReadOnlyList<RepositoryContent>>>? GetNotesFunc { get; set; }
    public Func<GitHubRepositoryContext, string, Task<string>>? GetNoteContentFunc { get; set; }
    public Func<GitHubRepositoryContext, string, string, string, Task>? CreateOrUpdateNoteFunc { get; set; }
    public Func<GitHubRepositoryContext, string, string, Task>? DeleteNoteFunc { get; set; }
    public Func<GitHubRepositoryContext, string, string, string?, string, Task>? MoveNoteFunc { get; set; }
    public Func<GitHubRepositoryContext, string, byte[], string, Task>? UploadBinaryFileFunc { get; set; }
    public List<(string Path, byte[] Content, string CommitMessage)> UploadedBinaries { get; } = new();
    public Func<GitHubRepositoryContext, string?, Task<byte[]>>? GetRepositoryArchiveFunc { get; set; }
    public List<(GitHubRepositoryContext Context, string? Reference)> ArchiveRequests { get; } = new();

    public Func<GitHubRepositoryContext, string, Task<IReadOnlyList<NoteCommitInfo>>>? GetCommitHistoryFunc { get; set; }
    public List<(GitHubRepositoryContext Context, string Path)> HistoryRequests { get; } = new();
    public Func<GitHubRepositoryContext, string, string, Task<string>>? GetNoteContentAtCommitFunc { get; set; }
    public List<(GitHubRepositoryContext Context, string Path, string Sha)> ContentAtCommitRequests { get; } = new();
    public List<(GitHubRepositoryContext Context, string Path, string Content, string CommitMessage)> SavedNotes { get; } = new();

    public Task<IReadOnlyList<RepositoryContent>> GetNotesAsync(GitHubRepositoryContext context, string? path = null)
    {
        return GetNotesFunc != null
            ? GetNotesFunc(context, path)
            : Task.FromResult<IReadOnlyList<RepositoryContent>>(new List<RepositoryContent>());
    }

    public Task<string> GetNoteContentAsync(GitHubRepositoryContext context, string path)
    {
        return GetNoteContentFunc != null
            ? GetNoteContentFunc(context, path)
            : Task.FromResult(string.Empty);
    }

    public Task CreateOrUpdateNoteAsync(
        GitHubRepositoryContext context,
        string path,
        string content,
        string commitMessage
    )
    {
        SavedNotes.Add((context, path, content, commitMessage));
        return CreateOrUpdateNoteFunc != null
            ? CreateOrUpdateNoteFunc(context, path, content, commitMessage)
            : Task.CompletedTask;
    }

    public Task<IReadOnlyList<NoteCommitInfo>> GetCommitHistoryAsync(GitHubRepositoryContext context, string path)
    {
        HistoryRequests.Add((context, path));
        return GetCommitHistoryFunc != null
            ? GetCommitHistoryFunc(context, path)
            : Task.FromResult<IReadOnlyList<NoteCommitInfo>>(new List<NoteCommitInfo>());
    }

    public Task<string> GetNoteContentAtCommitAsync(GitHubRepositoryContext context, string path, string sha)
    {
        ContentAtCommitRequests.Add((context, path, sha));
        return GetNoteContentAtCommitFunc != null
            ? GetNoteContentAtCommitFunc(context, path, sha)
            : Task.FromResult(string.Empty);
    }

    public Task DeleteNoteAsync(GitHubRepositoryContext context, string path, string commitMessage)
    {
        return DeleteNoteFunc != null
            ? DeleteNoteFunc(context, path, commitMessage)
            : Task.CompletedTask;
    }

    public Task MoveNoteAsync(
        GitHubRepositoryContext context,
        string fromPath,
        string toPath,
        string? content,
        string commitMessage
    )
    {
        return MoveNoteFunc != null
            ? MoveNoteFunc(context, fromPath, toPath, content, commitMessage)
            : Task.CompletedTask;
    }

    public Task UploadBinaryFileAsync(
        GitHubRepositoryContext context,
        string path,
        byte[] contentBytes,
        string commitMessage
    )
    {
        UploadedBinaries.Add((path, contentBytes, commitMessage));
        return UploadBinaryFileFunc != null
            ? UploadBinaryFileFunc(context, path, contentBytes, commitMessage)
            : Task.CompletedTask;
    }

    public Task<byte[]> GetRepositoryArchiveAsync(GitHubRepositoryContext context, string? reference = null)
    {
        ArchiveRequests.Add((context, reference));
        return GetRepositoryArchiveFunc != null
            ? GetRepositoryArchiveFunc(context, reference)
            : Task.FromResult(Array.Empty<byte>());
    }
}