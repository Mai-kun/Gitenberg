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

    public async Task MoveNoteAsync(
        GitHubRepositoryContext context,
        string fromPath,
        string toPath,
        string? content,
        string commitMessage
    )
    {
        var normalizedFrom = fromPath.Trim('/');
        var normalizedTo = toPath.Trim('/');
        if (string.Equals(normalizedFrom, normalizedTo, StringComparison.OrdinalIgnoreCase))
        {
            return; // nothing to move
        }

        // Moving a folder inside itself (a/b → a/b/c) would loop forever.
        if (normalizedTo.StartsWith(normalizedFrom + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Cannot move '{fromPath}' inside itself.");
        }

        var client = CreateClient(context.Token);

        var contents = await client.Repository.Content.GetAllContents(context.Owner, context.Repo, normalizedFrom);
        var first = contents.Count > 0 ? contents[0] : null;
        var isFile = first != null
            && first.Type == ContentType.File
            && string.Equals(first.Path?.Trim('/'), normalizedFrom, StringComparison.OrdinalIgnoreCase);

        // A caller-supplied content only makes sense for single files.
        if (!isFile && content != null)
        {
            throw new ArgumentException($"Path '{fromPath}' is a directory, not a movable file.");
        }

        if (!isFile)
        {
            await MoveFolderRecursiveAsync(client, context, normalizedFrom, normalizedTo, commitMessage);
            return;
        }

        // Resolve the payload: caller-supplied content (edited in the editor)
        // wins, otherwise read the existing file.
        if (content is null)
        {
            // Content is omitted for files > 1 MB — fall back to the blob API.
            content = first.Content;
            if (string.IsNullOrEmpty(content))
            {
                var blob = await client.Git.Blob.Get(context.Owner, context.Repo, first.Sha);
                content = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob.Content));
            }
        }

        // GitHub has no move API: write the new file, then remove the old one.
        // Removing after the write keeps the note alive if the write fails.
        await CreateOrUpdateNoteAsync(context, normalizedTo, content, commitMessage);

        var current = await client.Repository.Content.GetAllContents(context.Owner, context.Repo, normalizedFrom);
        if (current.Count > 0)
        {
            await client.Repository.Content.DeleteFile(
                context.Owner,
                context.Repo,
                current[0].Path,
                new DeleteFileRequest(commitMessage, current[0].Sha)
            );
        }
    }

    private async Task MoveFolderRecursiveAsync(
        GitHubClient client,
        GitHubRepositoryContext context,
        string fromDir,
        string toDir,
        string commitMessage
    )
    {
        var contents = await client.Repository.Content.GetAllContents(context.Owner, context.Repo, fromDir);
        foreach (var item in contents)
        {
            var itemTarget = $"{toDir}/{item.Name}";
            if (item.Type == ContentType.Dir)
            {
                await MoveFolderRecursiveAsync(client, context, item.Path, itemTarget, commitMessage);
            }
            else
            {
                var content = item.Content;
                if (string.IsNullOrEmpty(content))
                {
                    var blob = await client.Git.Blob.Get(context.Owner, context.Repo, item.Sha);
                    content = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob.Content));
                }

                await CreateOrUpdateNoteAsync(context, itemTarget, content, commitMessage);
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