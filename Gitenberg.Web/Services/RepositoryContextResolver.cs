using System.Security.Cryptography;
using Gitenberg.Web.Database;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Gitenberg.Web.Services;

public class RepositoryContextResolver(
    AppDbContext dbContext,
    ITokenEncryptionService encryptionService,
    ILogger<RepositoryContextResolver> logger
) : IRepositoryContextResolver
{
    public async Task<ResolvedRepository?> ResolveActiveAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == userId, cancellationToken);
        if (user == null)
        {
            return null;
        }

        if (user.SelectedRepositoryId is { } selectedId)
        {
            var selected = await ResolveByIdAsync(userId, selectedId, cancellationToken);
            if (selected != null)
            {
                return selected;
            }
        }

        // Selection missing or stale (repository deleted elsewhere) — first wins.
        var first = await dbContext.Repositories
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync(r => r.TelegramUserId == userId, cancellationToken);
        return first == null ? null : Build(first);
    }

    public async Task<ResolvedRepository?> ResolveByIdAsync(long userId, int repositoryId, CancellationToken cancellationToken = default)
    {
        var repository = await dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId && r.TelegramUserId == userId, cancellationToken);
        return repository == null ? null : Build(repository);
    }

    private ResolvedRepository? Build(Repository repository)
    {
        if (string.IsNullOrWhiteSpace(repository.GitHubToken))
        {
            return null;
        }

        try
        {
            return new ResolvedRepository(repository, encryptionService.DecryptToken(repository.GitHubToken));
        }
        catch (CryptographicException ex)
        {
            // Keys rotated or data migrated between machines: the repository
            // must be re-registered with a fresh token.
            logger.LogWarning(
                ex,
                "GitHub token for repository {RepositoryId} (user {TelegramId}) could not be decrypted.",
                repository.Id,
                repository.TelegramUserId
            );
            return null;
        }
    }
}
