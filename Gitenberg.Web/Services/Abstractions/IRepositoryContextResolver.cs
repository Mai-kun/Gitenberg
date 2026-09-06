using Gitenberg.Web.Models;

namespace Gitenberg.Web.Services.Abstractions;

/// <summary>
/// A user's repository together with its decrypted GitHub credentials, so
/// callers can reach both the API context and the per-repository settings
/// (inbox path, attachments path, display name).
/// </summary>
public sealed record ResolvedRepository(Repository Repository, string DecryptedToken)
{
    public int RepositoryId => Repository.Id;

    public GitHubRepositoryContext Context => new(DecryptedToken, Repository.RepositoryOwner, Repository.RepositoryName);
}

public interface IRepositoryContextResolver
{
    /// <summary>
    /// Resolves the user's currently selected repository (falls back to the
    /// first one when the selection is missing or stale). Returns null when
    /// the user has no usable repository.
    /// </summary>
    Task<ResolvedRepository?> ResolveActiveAsync(long telegramId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a specific repository owned by the user; null when it does not exist for them.</summary>
    Task<ResolvedRepository?> ResolveByIdAsync(long telegramId, int repositoryId, CancellationToken cancellationToken = default);
}
