using Gitenberg.Web.Models;

namespace Gitenberg.Web.Services.Abstractions;

public sealed record ResolvedRepository(Repository Repository, string DecryptedToken)
{
    public int RepositoryId => Repository.Id;

    public GitHubRepositoryContext Context => new(DecryptedToken, Repository.RepositoryOwner, Repository.RepositoryName);
}

public interface IRepositoryContextResolver
{
    Task<ResolvedRepository?> ResolveActiveAsync(long telegramId, CancellationToken cancellationToken = default);

    Task<ResolvedRepository?> ResolveByIdAsync(long telegramId, int repositoryId, CancellationToken cancellationToken = default);
}
