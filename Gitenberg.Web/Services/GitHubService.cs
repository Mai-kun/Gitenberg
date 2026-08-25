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
        var file = contents[0];
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
        var sha = contents[0].Sha;

        await client.Repository.Content.DeleteFile(
            context.Owner,
            context.Repo,
            path,
            new DeleteFileRequest(commitMessage, sha)
        );
    }

    private static GitHubClient CreateClient(string token)
    {
        return new GitHubClient(new ProductHeaderValue("Gitenberg"))
        {
            Credentials = new Credentials(token),
        };
    }
}