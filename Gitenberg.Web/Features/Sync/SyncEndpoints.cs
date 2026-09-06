using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Models;
using Gitenberg.Web.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Gitenberg.Web.Features.Sync;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync")
                       .WithTags("Sync")
                       .RequireTelegramAuth();

        group.MapGet("/status", Status)
             .WithName("SyncStatus")
             .WithSummary("Number of local changes of the active repository waiting to be synced to GitHub");

        group.MapPost("/", FlushNow)
             .WithName("SyncFlush")
             .WithSummary("Force-sync all pending local changes of every repository to GitHub right now");
    }

    public static async Task<IResult> Status(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        IRepositoryContextResolver repositoryResolver,
        PendingSyncService pendingSync,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        var repository = await repositoryResolver.ResolveActiveAsync(telegramId.Value);
        if (repository == null)
        {
            return Results.Ok(new { Pending = 0 });
        }

        return Results.Ok(new { Pending = await pendingSync.GetPendingCountAsync(telegramId.Value, repository.RepositoryId) });
    }

    public static async Task<IResult> FlushNow(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        PendingSyncService pendingSync,
        IGitHubService gitHubService,
        IRepositoryContextResolver repositoryResolver,
        IMemoryCache memoryCache,
        NoteIndexer indexer,
        HttpContext? httpContext = null
    )
    {
        var telegramId = TelegramAuthResolver.Resolve(httpContext, headerTelegramId, queryTelegramId);
        if (telegramId == null)
        {
            return Results.BadRequest(
                new { Error = "Telegram ID is required. Provide it in 'X-Telegram-Id' header or 'telegramId' query parameter." });
        }

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
        if (user == null)
        {
            return Results.NotFound(new { Error = $"User with Telegram ID {telegramId} not found." });
        }

        // Ops live per repository, and a previous session may have switched
        // repositories with unsynced changes — flush every repository, the
        // badge keeps tracking the active one.
        var repositories = await dbContext.Repositories
            .Where(r => r.TelegramUserId == telegramId)
            .OrderBy(r => r.Id)
            .ToListAsync();

        var appliedTotal = 0;
        var remainingTotal = 0;
        var activeRepositoryId = (await repositoryResolver.ResolveActiveAsync(telegramId.Value))?.RepositoryId;

        foreach (var repository in repositories)
        {
            var resolved = await repositoryResolver.ResolveByIdAsync(telegramId.Value, repository.Id);
            if (resolved == null)
            {
                // No usable token: leave this repository's ops queued.
                remainingTotal += await pendingSync.GetPendingCountAsync(telegramId.Value, repository.Id);
                continue;
            }

            var (applied, remaining) = await pendingSync.FlushUserAsync(telegramId.Value, repository.Id, resolved.Context, gitHubService);
            appliedTotal += applied;
            remainingTotal += remaining;
            if (applied > 0)
            {
                BustUserCache(memoryCache, telegramId.Value, repository.Id);
            }
        }

        try
        {
            // Re-index the active repository so search sees the freshly
            // flushed content.
            await indexer.SynchronizeUserByIdAsync(telegramId.Value, activeRepositoryId);
        }
        catch
        {
            // Search freshness is best-effort.
        }

        return Results.Ok(new { Applied = appliedTotal, Remaining = remainingTotal });
    }

    private static void BustUserCache(IMemoryCache memoryCache, long telegramId, int repositoryId)
    {
        var ctsKey = $"notes_cts_{telegramId}_{repositoryId}";
        if (memoryCache.TryGetValue(ctsKey, out CancellationTokenSource? cts))
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        memoryCache.Remove(ctsKey);
    }
}

/// <summary>
/// Periodically flushes pending local changes to GitHub.
/// </summary>
public class SyncFlushService(
    IServiceScopeFactory scopeFactory,
    IOptions<SyncConfiguration> options,
    ILogger<SyncFlushService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.FlushIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                try
                {
                    await FlushAllAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Pending sync flush cycle failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host shutdown requested.
        }
    }

    private async Task FlushAllAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pendingSync = scope.ServiceProvider.GetRequiredService<PendingSyncService>();
        var gitHubService = scope.ServiceProvider.GetRequiredService<IGitHubService>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<ITokenEncryptionService>();
        var memoryCache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();

        // Only repositories with queued ops are touched, and each is flushed
        // into its own GitHub repository with its own token.
        var queued = await dbContext.Database
            .SqlQuery<QueuedRepoRow>($"""
                SELECT CAST(TelegramUserId AS INTEGER) AS TelegramUserId, CAST(RepositoryId AS INTEGER) AS RepositoryId
                FROM PendingNoteOps
                GROUP BY TelegramUserId, RepositoryId
                """)
            .ToListAsync();

        foreach (var row in queued)
        {
            var telegramId = row.TelegramUserId;
            var repository = await dbContext.Repositories
                .FirstOrDefaultAsync(r => r.Id == row.RepositoryId && r.TelegramUserId == telegramId);
            if (repository?.GitHubToken == null || string.IsNullOrWhiteSpace(repository.GitHubToken)) continue;

            GitHubRepositoryContext context;
            try
            {
                context = new GitHubRepositoryContext(
                    encryptionService.DecryptToken(repository.GitHubToken), repository.RepositoryOwner, repository.RepositoryName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping flush for repository {RepositoryId}: token could not be decrypted.", repository.Id);
                continue;
            }

            var (applied, remaining) = await pendingSync.FlushUserAsync(telegramId, repository.Id, context, gitHubService);
            if (applied > 0)
            {
                BustUserCache(memoryCache, telegramId, repository.Id);
                logger.LogInformation("Flushed {Applied} pending ops for user {TelegramId} repository {RepositoryId} ({Remaining} left).",
                    applied, telegramId, repository.Id, remaining);
            }
        }
    }

    private sealed record QueuedRepoRow(long TelegramUserId, int RepositoryId);

    private static void BustUserCache(IMemoryCache memoryCache, long telegramId, int repositoryId)
    {
        var ctsKey = $"notes_cts_{telegramId}_{repositoryId}";
        if (memoryCache.TryGetValue(ctsKey, out CancellationTokenSource? cts))
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        memoryCache.Remove(ctsKey);
    }
}

public class SyncConfiguration
{
    public const string SectionName = "Sync";

    /// <summary>How often pending local changes are pushed to GitHub (minutes).</summary>
    public int FlushIntervalMinutes { get; set; } = 5;
}
