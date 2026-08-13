using Dispatch.Web.Data;
using Dispatch.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Workers;

public class FeedSessionCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FeedCoordinator _coordinator;
    private readonly ILogger<FeedSessionCleanupWorker> _logger;

    public FeedSessionCleanupWorker(IServiceScopeFactory scopeFactory, FeedCoordinator coordinator, ILogger<FeedSessionCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await StopFeedsWithNoActiveListenersAsync(stoppingToken);
                await PurgeStaleRecordsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during feed session cleanup.");
            }
        }
    }

    private async Task StopFeedsWithNoActiveListenersAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

        var cutoff = DateTime.UtcNow.AddMinutes(-5);

        // Find feeds that have canvas entries but ALL of them are stale (no active heartbeat)
        var feedsWithAnyEntry = await db.UserActiveFeeds
            .Where(u => u.LastHeartbeatUtc > cutoff)
            .Select(u => u.FeedId)
            .Distinct()
            .ToListAsync(ct);

        var activeFeedIdSet = feedsWithAnyEntry.ToHashSet();

        // Find running feeds that have no active listener
        var allCanvasFeedIds = await db.UserActiveFeeds
            .Select(u => u.FeedId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var feedId in allCanvasFeedIds)
        {
            if (!_coordinator.IsRunning(feedId)) continue;
            if (activeFeedIdSet.Contains(feedId)) continue; // Has active listener

            var feed = await db.Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.Id == feedId, ct);
            if (feed == null || feed.IsActive) continue; // AdminPinned — keep running

            _logger.LogInformation("Stopping feed {FeedId} — no active listeners.", feedId);
            await _coordinator.StopAsync(feedId);
        }
    }

    private async Task PurgeStaleRecordsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

        // Delete UserActiveFeed records that have been stale for over 24 hours.
        // These are from users who logged out or abandoned their sessions long ago.
        var purgeCutoff = DateTime.UtcNow.AddHours(-24);
        var staleRecords = await db.UserActiveFeeds
            .Where(u => u.LastHeartbeatUtc < purgeCutoff)
            .ToListAsync(ct);

        if (staleRecords.Count > 0)
        {
            db.UserActiveFeeds.RemoveRange(staleRecords);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Purged {Count} stale UserActiveFeed records.", staleRecords.Count);
        }
    }
}
