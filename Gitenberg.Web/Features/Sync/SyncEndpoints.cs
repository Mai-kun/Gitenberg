using Gitenberg.Web.Database;
using Gitenberg.Web.Features.Search;
using Gitenberg.Web.Features.TelegramBot.Auth;
using Gitenberg.Web.Infrastructure;
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
             .WithSummary("Number of local changes waiting to be synced to GitHub");

        group.MapPost("/", FlushNow)
             .WithName("SyncFlush")
             .WithSummary("Force-sync all pending local changes to GitHub right now");
    }

    public static async Task<IResult> Status(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
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

        return Results.Ok(new { Pending = await pendingSync.GetPendingCountAsync(telegramId.Value) });
    }

    public static async Task<IResult> FlushNow(
        [FromHeader(Name = "X-Telegram-Id")] long? headerTelegramId,
        [FromQuery(Name = "telegramId")] long? queryTelegramId,
        AppDbContext dbContext,
        PendingSyncService pendingSync,
        IGitHubService gitHubService,
        ITokenEncryptionService encryptionService,
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

        if (string.IsNullOrWhiteSpace(user.GitHubToken))
        {
            return Results.BadRequest(new { Error = "GitHub token is not configured for this user." });
        }

        var context = new GitHubRepositoryContext(
            encryptionService.DecryptToken(user.GitHubToken), user.RepositoryOwner, user.RepositoryName);

        var (applied, remaining) = await pendingSync.FlushUserAsync(telegramId.Value, context, gitHubService);
        BustUserCache(memoryCache, telegramId.Value);

        try
        {
            // Re-index so the search sees the freshly flushed content.
            await indexer.SynchronizeUserByIdAsync(telegramId.Value);
        }
        catch
        {
            // Search freshness is best-effort.
        }

        return Results.Ok(new { Applied = applied, Remaining = remaining });
    }

    private static void BustUserCache(IMemoryCache memoryCache, long telegramId)
    {
        var ctsKey = $"notes_cts_{telegramId}";
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

        var userIds = await dbContext.Database
            .SqlQuery<string>($"SELECT DISTINCT TelegramUserId AS Value FROM PendingNoteOps")
            .ToListAsync();

        foreach (var uid in userIds)
        {
            if (!long.TryParse(uid, out var telegramId)) continue;
            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);
            if (user?.GitHubToken == null || string.IsNullOrWhiteSpace(user.GitHubToken)) continue;

            var context = new GitHubRepositoryContext(
                encryptionService.DecryptToken(user.GitHubToken), user.RepositoryOwner, user.RepositoryName);
            var (applied, remaining) = await pendingSync.FlushUserAsync(telegramId, context, gitHubService);
            if (applied > 0)
            {
                BustUserCache(memoryCache, telegramId);
                logger.LogInformation("Flushed {Applied} pending ops for user {TelegramId} ({Remaining} left).",
                    applied, telegramId, remaining);
            }
        }
    }

    private static void BustUserCache(IMemoryCache memoryCache, long telegramId)
    {
        var ctsKey = $"notes_cts_{telegramId}";
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
