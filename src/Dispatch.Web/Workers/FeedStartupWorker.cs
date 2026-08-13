using Dispatch.Web.Data;
using Dispatch.Web.Models;
using Dispatch.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Workers;

public class FeedStartupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FeedCoordinator _coordinator;
    private readonly ILogger<FeedStartupWorker> _logger;

    public FeedStartupWorker(IServiceScopeFactory scopeFactory, FeedCoordinator coordinator, ILogger<FeedStartupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

            // Start admin-pinned feeds
            var pinnedFeeds = await db.Feeds
                .Where(f => f.IsVisible && f.IsActive && !f.AdminStopped)
                .ToListAsync(stoppingToken);

            // Also start feeds that have active user sessions (from before restart)
            var userActiveFeedIds = await db.UserActiveFeeds
                .Select(u => u.FeedId)
                .Distinct()
                .ToListAsync(stoppingToken);

            var sessionFeeds = userActiveFeedIds.Count > 0
                ? await db.Feeds
                    .Where(f => f.IsVisible && !f.AdminStopped && !f.IsActive && userActiveFeedIds.Contains(f.Id))
                    .ToListAsync(stoppingToken)
                : new List<Feed>();

            await _coordinator.StartActiveFeedsAsync(pinnedFeeds.Concat(sessionFeeds), stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start active feeds.");
        }
    }
}
