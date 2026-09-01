namespace Gitenberg.Web.Features.Search;

/// <summary>
/// Periodically refreshes the local FTS5 note index in the background.
/// Runs one cycle immediately at startup and then every IndexingIntervalMinutes.
/// </summary>
public class NoteIndexingService(
    IServiceScopeFactory scopeFactory,
    SearchConfiguration configuration,
    ILogger<NoteIndexingService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.IndexingIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                try
                {
                    await RunIndexingCycleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Note indexing cycle failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host shutdown requested.
        }
    }

    private async Task RunIndexingCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var indexer = scope.ServiceProvider.GetRequiredService<NoteIndexer>();
        await indexer.SynchronizeAllUsersAsync(cancellationToken);
        logger.LogInformation("Note indexing cycle completed.");
    }
}
