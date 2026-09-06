using System.Text;
using FluentAssertions;
using Gitenberg.Tests.Mocks;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services;
using Octokit;
using Xunit;

namespace Gitenberg.Tests.Services;

/// <summary>
/// Offline unit tests for the version-history methods of GitHubService,
/// routed through the FakeOctokitGitHubClient DispatchProxy.
/// </summary>
public class GitHubServiceTests
{
    private readonly GitHubRepositoryContext _context = new("token", "owner", "repo");

    private static IGitHubClient Wrap(FakeOctokitGitHubClient fake)
    {
        return (IGitHubClient)(object)fake;
    }

    // Octokit models expose only getters, so fixtures go through the public ctors.
    private static Committer MakeCommitter(string name, string dateIso)
    {
        return new Committer(name, $"{name.ToLowerInvariant()}@example.com", DateTimeOffset.Parse(dateIso));
    }

    private static Commit MakeCommit(string message, Committer author)
    {
        return new Commit(null!, null!, null!, null!, null!, null!, null!, message, author, author, null!, Array.Empty<GitReference>(), 0, null!);
    }

    private static GitHubCommit MakeGitHubCommit(string sha, Commit commit, Author? author)
    {
        return new GitHubCommit(null!, null!, null!, null!, sha, null!, null!, author, null!, commit, author, null!, null!, Array.Empty<GitReference>(), Array.Empty<GitHubCommitFile>());
    }

    private static Author MakeAuthor(string login, string avatarUrl)
    {
        return new Author(login, 1, null!, avatarUrl, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, false);
    }

    private static RepositoryContent MakeContent(
        string name,
        string path,
        string sha,
        string? content,
        int size = 10
    )
    {
        var constructor = typeof(RepositoryContent).GetConstructors()
            .First(c => c.GetParameters().Length > 0);
        var parameters = constructor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var nameLower = parameters[i].Name?.ToLowerInvariant();
            args[i] = nameLower switch
            {
                "name" => name,
                "path" => path,
                "sha" => sha,
                "size" => size,
                "type" => ContentType.File,
                // The Content getter always decodes EncodedContent as base64;
                // an empty string means "content omitted" (files > 1 MB).
                "encodedcontent" => content is null ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                "encoding" => "base64",
                "url" => "http://dummy/url",
                "giturl" => "http://dummy/giturl",
                "htmlurl" => "http://dummy/html",
                "downloadurl" => "http://dummy/download",
                _ => null,
            };
        }

        return (RepositoryContent)constructor.Invoke(args);
    }

    [Fact]
    public async Task GetCommitHistoryAsync_ShouldTrimLeadingSlash_AndMapCommits()
    {
        var fake = FakeOctokitGitHubClient.Create();
        var service = new GitHubService(Wrap(fake));

        fake.CommitsToReturn.Add(MakeGitHubCommit(
            "sha2",
            MakeCommit("Update note: notes/idea.md", MakeCommitter("Alice", "2026-01-02T10:00:00Z")),
            MakeAuthor("alice", "http://avatar")));
        fake.CommitsToReturn.Add(MakeGitHubCommit(
            "sha1",
            MakeCommit("Create note", MakeCommitter("Bob", "2026-01-01T10:00:00Z")),
            null));

        var result = await service.GetCommitHistoryAsync(_context, "/notes/idea.md");

        // GitHub returns an empty history for paths with a leading slash.
        fake.CommitQueries.Should().ContainSingle();
        fake.CommitQueries[0].Owner.Should().Be("owner");
        fake.CommitQueries[0].Repo.Should().Be("repo");
        fake.CommitQueries[0].Request.Path.Should().Be("notes/idea.md");

        result.Should().HaveCount(2);
        result[0].Sha.Should().Be("sha2");
        result[0].AuthorName.Should().Be("Alice");
        result[0].AuthorLogin.Should().Be("alice");
        result[0].AuthorAvatarUrl.Should().Be("http://avatar");
        result[0].Date.Should().Be(DateTimeOffset.Parse("2026-01-02T10:00:00Z"));
        result[0].Message.Should().Be("Update note: notes/idea.md");

        // A commit without a GitHub account maps to nulls, not exceptions.
        result[1].AuthorLogin.Should().BeNull();
        result[1].AuthorAvatarUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetNoteContentAtCommitAsync_ShouldReturnContent_AndPassReference()
    {
        var fake = FakeOctokitGitHubClient.Create();
        var service = new GitHubService(Wrap(fake));

        fake.ContentsByRefToReturn.Add(
            MakeContent("idea.md", "notes/idea.md", "filesha", "# old version"));

        var content = await service.GetNoteContentAtCommitAsync(_context, "notes/idea.md", "abcdef1234567890");

        content.Should().Be("# old version");
        fake.ContentsByRefQueries.Should().ContainSingle();
        fake.ContentsByRefQueries[0].Path.Should().Be("notes/idea.md");
        fake.ContentsByRefQueries[0].Reference.Should().Be("abcdef1234567890");
    }

    [Fact]
    public async Task GetNoteContentAtCommitAsync_ShouldTrimLeadingSlash()
    {
        var fake = FakeOctokitGitHubClient.Create();
        var service = new GitHubService(Wrap(fake));

        fake.ContentsByRefToReturn.Add(
            MakeContent("idea.md", "notes/idea.md", "filesha", "# old version"));

        await service.GetNoteContentAtCommitAsync(_context, "/notes/idea.md", "abcdef1");

        fake.ContentsByRefQueries[0].Path.Should().Be("notes/idea.md");
    }

    [Fact]
    public async Task GetNoteContentAtCommitAsync_ShouldThrowNotFound_WhenFileIsMissingAtCommit()
    {
        var fake = FakeOctokitGitHubClient.Create();
        var service = new GitHubService(Wrap(fake));
        // No entries returned: the file did not exist at that commit.

        Func<Task> act = () => service.GetNoteContentAtCommitAsync(_context, "notes/idea.md", "abcdef1");

        await act.Should().ThrowAsync<NotFoundException>();
        fake.BlobQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetNoteContentAtCommitAsync_ShouldFallBackToBlob_WhenContentIsOmitted()
    {
        var fake = FakeOctokitGitHubClient.Create();
        var service = new GitHubService(Wrap(fake));

        // Files > 1 MB come back without Content; only the blob SHA is known.
        fake.ContentsByRefToReturn.Add(
            MakeContent("big.md", "notes/big.md", "filesha", content: null));
        fake.BlobToReturn = new Blob(
            null!,
            Convert.ToBase64String(Encoding.UTF8.GetBytes("# large file")),
            EncodingType.Base64,
            "blobsha",
            13);

        var content = await service.GetNoteContentAtCommitAsync(_context, "notes/big.md", "abcdef1");

        content.Should().Be("# large file");
        fake.BlobQueries.Should().ContainSingle();
        fake.BlobQueries[0].Sha.Should().Be("filesha");
    }
}
