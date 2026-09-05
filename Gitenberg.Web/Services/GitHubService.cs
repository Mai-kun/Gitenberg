using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Octokit;

namespace Gitenberg.Web.Services;

public class GitHubService : IGitHubService
{
    public async Task<IReadOnlyList<RepositoryContent>> GetNotesAsync(
        GitHubRepositoryContext context,
        string? path = null
    )
    {
        var client = CreateClient(context.Token);
        return string.IsNullOrEmpty(path)
            ? await client.Repository.Content.GetAllContents(context.Owner, context.Repo)
            : await client.Repository.Content.GetAllContents(context.Owner, context.Repo, path);
    }

    public async Task<string> GetNoteContentAsync(GitHubRepositoryContext context, string path)
    {
        var client = CreateClient(context.Token);
        var contents = await client.Repository.Content.GetAllContents(context.Owner, context.Repo, path);

        // Directory paths must not be read as notes. GitHub returns either:
        //  - a single file entry (Type=File), or
        //  - the directory's children (Type=Dir on entries / null Content / path mismatch).
        if (contents is null || contents.Count == 0)
        {
            throw new ArgumentException($"Path '{path}' is a directory, not a readable file.");
        }

        var file = contents[0];
        if (file.Type == ContentType.Dir || string.IsNullOrEmpty(file.Content))
        {
            // Distinguish a true directory from a large file whose Content is omitted (>1 MB).
            // Large files still have Type=File and a matching path; directories do not.
            var normalizedPath = path.Trim('/');
            var pathMatches = string.Equals(
                file.Path?.Trim('/'),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase
            );
            var looksLikeDirectory =
                file.Type == ContentType.Dir
                || contents.Count > 1
                || !pathMatches
                || file.Type != ContentType.File;

            if (looksLikeDirectory)
            {
                throw new ArgumentException($"Path '{path}' is a directory, not a readable file.");
            }
        }

        // Content may still be null/empty for very large files; callers handle that.
        return file.Content;
    }

    public async Task CreateOrUpdateNoteAsync(
        GitHubRepositoryContext context,
        string path,
        string content,
        string commitMessage
    )
    {
        var client = CreateClient(context.Token);

        try
        {
            var existing = await client.Repository.Content.GetAllContents(context.Owner, context.Repo, path);
            var sha = existing[0].Sha;

            await client.Repository.Content.UpdateFile(
                context.Owner,
                context.Repo,
                path,
                new UpdateFileRequest(commitMessage, content, sha)
            );
        }
        catch (NotFoundException)
        {
            await client.Repository.Content.CreateFile(
                context.Owner,
                context.Repo,
                path,
                new CreateFileRequest(commitMessage, content)
            );
        }
    }

    public async Task DeleteNoteAsync(GitHubRepositoryContext context, string path, string commitMessage)
    {
        var client = CreateClient(context.Token);
        var contents = await client.Repository.Content.GetAllContents(context.Owner, context.Repo, path);

        // A file path returns a single entry whose Path matches the request;
        // anything else (multiple children, a Dir entry, an empty result) is
        // treated as a folder and deleted recursively.
        var first = contents.FirstOrDefault();
        var normalizedPath = path.Trim('/');
        var isFile = first != null
            && first.Type == ContentType.File
            && string.Equals(first.Path?.Trim('/'), normalizedPath, StringComparison.OrdinalIgnoreCase);

        if (isFile)
        {
            await client.Repository.Content.DeleteFile(
                context.Owner,
                context.Repo,
                first.Path,
                new DeleteFileRequest(commitMessage, first.Sha)
            );
            return;
        }

        await DeleteFolderRecursiveAsync(client, context, path, commitMessage);
    }

    private static async Task DeleteFolderRecursiveAsync(
        GitHubClient client,
        GitHubRepositoryContext context,
        string path,
        string commitMessage
    )
    {
        // GitHub has no folder-delete API: remove every file bottom-up.
        // Sequential commits avoid ref conflicts from parallel deletions.
        var contents = await client.Repository.Content.GetAllContents(context.Owner, context.Repo, path);
        foreach (var item in contents)
        {
            if (item.Type == ContentType.Dir)
            {
                await DeleteFolderRecursiveAsync(client, context, item.Path, commitMessage);
            }
            else
            {
                await client.Repository.Content.DeleteFile(
                    context.Owner,
                    context.Repo,
                    item.Path,
                    new DeleteFileRequest(commitMessage, item.Sha)
                );
            }
        }
    }

    private static GitHubClient CreateClient(string token)
    {
        return new GitHubClient(new ProductHeaderValue("Gitenberg"))
        {
            Credentials = new Credentials(token),
        };
    }
}