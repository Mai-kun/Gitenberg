using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Octokit;

namespace Gitenberg.Tests.Mocks;

/// <summary>
/// Recorded CreateFile call routed through the Octokit content API.
/// </summary>
public record CreatedFile(string Owner, string Repo, string Path, CreateFileRequest Request);

/// <summary>Recorded Repository.Commit.GetAll call (version history).</summary>
public record CommitQuery(string Owner, string Repo, CommitRequest Request, ApiOptions Options);

/// <summary>Recorded Repository.Content.GetAllContentsByRef call (content at a commit).</summary>
public record ContentsByRefQuery(string Owner, string Repo, string Path, string Reference);

/// <summary>Recorded Git.Blob.Get call (large-file fallback).</summary>
public record BlobQuery(string Owner, string Repo, string Sha);

/// <summary>
/// Hand-rolled Octokit fake built with DispatchProxy: the
/// Repository.Content.CreateFile path is implemented and recorded, plus the
/// members needed for version history (Commit.GetAll, GetAllContentsByRef,
/// Git.Blob.Get); everything else throws.
/// </summary>
public class FakeOctokitGitHubClient : DispatchProxy
{
    private static readonly MethodInfo DispatchProxyCreate = typeof(DispatchProxy)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2);

    private readonly List<CreatedFile> _createdFiles = new();
    private readonly List<CommitQuery> _commitQueries = new();
    private readonly List<ContentsByRefQuery> _contentsByRefQueries = new();
    private readonly List<BlobQuery> _blobQueries = new();

    public IReadOnlyList<CreatedFile> CreatedFiles => _createdFiles;
    public IReadOnlyList<CommitQuery> CommitQueries => _commitQueries;
    public IReadOnlyList<ContentsByRefQuery> ContentsByRefQueries => _contentsByRefQueries;
    public IReadOnlyList<BlobQuery> BlobQueries => _blobQueries;

    public List<GitHubCommit> CommitsToReturn { get; } = new();
    public List<RepositoryContent> ContentsByRefToReturn { get; } = new();
    public Blob? BlobToReturn { get; set; }

    public static FakeOctokitGitHubClient Create()
    {
        var proxy = DispatchProxy.Create<IGitHubClient, FakeOctokitGitHubClient>()!;
        return (FakeOctokitGitHubClient)(object)proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        // Top level = IGitHubClient: route each member to its sub-client proxy.
        if (targetMethod?.Name == "get_Repository")
        {
            return CreateProxy(typeof(IRepositoriesClient), HandleRepositoryMember);
        }

        if (targetMethod?.Name == "get_Git")
        {
            return CreateProxy(typeof(IGitDatabaseClient), HandleGitMember);
        }

        throw new NotSupportedException($"Octokit member {targetMethod?.Name} is not supported by the fake.");
    }

    private object? HandleRepositoryMember(MethodInfo method, object?[] args, Func<object?> next)
    {
        if (method.Name == "get_Content")
        {
            return CreateProxy(typeof(IRepositoryContentsClient), HandleContentMember);
        }

        if (method.Name == "get_Commit")
        {
            return CreateProxy(typeof(IRepositoryCommitsClient), HandleCommitMember);
        }

        throw new NotSupportedException($"Octokit member {method.Name} is not supported by the fake.");
    }

    private object? HandleCommitMember(MethodInfo method, object?[] args, Func<object?> next)
    {
        if (method.Name == "GetAll")
        {
            var request = (CommitRequest)args[2]!;
            var options = (ApiOptions)args[3]!;
            _commitQueries.Add(new CommitQuery((string)args[0]!, (string)args[1]!, request, options));
            return Task.FromResult<IReadOnlyList<GitHubCommit>>(CommitsToReturn);
        }

        throw new NotSupportedException($"Octokit commit member {method.Name} is not supported by the fake.");
    }

    private object? HandleGitMember(MethodInfo method, object?[] args, Func<object?> next)
    {
        if (method.Name == "get_Blob")
        {
            return CreateProxy(typeof(IBlobsClient), HandleBlobMember);
        }

        throw new NotSupportedException($"Octokit git member {method.Name} is not supported by the fake.");
    }

    private object? HandleBlobMember(MethodInfo method, object?[] args, Func<object?> next)
    {
        if (method.Name == "Get")
        {
            _blobQueries.Add(new BlobQuery((string)args[0]!, (string)args[1]!, (string)args[2]!));
            return Task.FromResult(BlobToReturn ?? throw new NotFoundException(
                "Blob not found by the fake.",
                System.Net.HttpStatusCode.NotFound));
        }

        throw new NotSupportedException($"Octokit blob member {method.Name} is not supported by the fake.");
    }

    private object? HandleContentMember(MethodInfo method, object?[] args, Func<object?> next)
    {
        if (method.Name == "CreateFile")
        {
            var request = (CreateFileRequest)args[3]!;
            _createdFiles.Add(new CreatedFile((string)args[0]!, (string)args[1]!, (string)args[2]!, request));
            return Task.FromResult(new RepositoryContentChangeSet());
        }

        if (method.Name == "GetAllContentsByRef")
        {
            _contentsByRefQueries.Add(new ContentsByRefQuery(
                (string)args[0]!, (string)args[1]!, (string)args[2]!, (string)args[3]!));
            return Task.FromResult<IReadOnlyList<RepositoryContent>>(ContentsByRefToReturn);
        }

        throw new NotSupportedException($"Octokit content member {method.Name} is not supported by the fake.");
    }

    private static object CreateProxy(Type interfaceType, Func<MethodInfo, object?[], Func<object?>, object?> handler)
    {
        var proxy = DispatchProxyCreate.MakeGenericMethod(interfaceType, typeof(RecordingInterceptor))
                                       .Invoke(null, null)!;
        ((RecordingInterceptor)proxy).Handler = handler;
        return proxy;
    }

    private class RecordingInterceptor : DispatchProxy
    {
        public Func<MethodInfo, object?[], Func<object?>, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException("Null method in proxy.");
            return Handler(method, args ?? Array.Empty<object?>(), () => null);
        }
    }
}
