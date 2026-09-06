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

/// <summary>
/// Hand-rolled Octokit fake built with DispatchProxy: only the
/// Repository.Content.CreateFile path is implemented and recorded;
/// everything else throws.
/// </summary>
public class FakeOctokitGitHubClient : DispatchProxy
{
    private static readonly MethodInfo DispatchProxyCreate = typeof(DispatchProxy)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2);

    private readonly List<CreatedFile> _createdFiles = new();

    public IReadOnlyList<CreatedFile> CreatedFiles => _createdFiles;

    public static FakeOctokitGitHubClient Create()
    {
        var proxy = DispatchProxy.Create<IGitHubClient, FakeOctokitGitHubClient>()!;
        return (FakeOctokitGitHubClient)(object)proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        return CreateProxy(typeof(IRepositoriesClient), HandleRepositoryMember);
    }

    private object? HandleRepositoryMember(MethodInfo method, object?[] args, Func<object?> next)
    {
        if (method.Name == "get_Content")
        {
            return CreateProxy(typeof(IRepositoryContentsClient), HandleContentMember);
        }

        throw new NotSupportedException($"Octokit member {method.Name} is not supported by the fake.");
    }

    private object? HandleContentMember(MethodInfo method, object?[] args, Func<object?> next)
    {
        if (method.Name == "CreateFile")
        {
            var request = (CreateFileRequest)args[3]!;
            _createdFiles.Add(new CreatedFile((string)args[0]!, (string)args[1]!, (string)args[2]!, request));
            return Task.FromResult(new RepositoryContentChangeSet());
        }

        throw new NotSupportedException($"Octokit member {method.Name} is not supported by the fake.");
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
