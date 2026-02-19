using Dispatch.Web.Data;
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
            var feeds = await db.Feeds.Where(f => f.IsActive && f.IsVisible).ToListAsync(stoppingToken);

            await _coordinator.StartActiveFeedsAsync(feeds, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start active feeds.");
        }
    }
}
