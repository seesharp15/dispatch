using System.Collections.Concurrent;
using Dispatch.Web.Models;
using Microsoft.Extensions.Logging;

namespace Dispatch.Web.Services;

public class FeedCoordinator
{
    private readonly ConcurrentDictionary<Guid, FeedWorker> _workers = new();
    private readonly FeedRecorder _recorder;
    private readonly IFeedEventHub _eventHub;
    private readonly ILogger<FeedCoordinator> _logger;

    public FeedCoordinator(FeedRecorder recorder, IFeedEventHub eventHub, ILogger<FeedCoordinator> logger)
    {
        _recorder = recorder;
        _eventHub = eventHub;
        _logger = logger;
    }

    public bool IsRunning(Guid feedId) => _workers.ContainsKey(feedId);

    public Task<bool> StartAsync(Feed feed, CancellationToken cancellationToken, bool? isActive = null)
    {
        if (_workers.ContainsKey(feed.Id))
        {
            return Task.FromResult(false);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var worker = new FeedWorker(cts, Task.Run(() => _recorder.RunAsync(feed, cts.Token), cts.Token));
        if (!_workers.TryAdd(feed.Id, worker))
        {
            cts.Cancel();
            return Task.FromResult(false);
        }

        _ = worker.Task.ContinueWith(task =>
        {
            _workers.TryRemove(feed.Id, out _);
            _ = _eventHub.PublishAsync(new FeedStatusEvent(feed.Id, false, null));
            if (task.Exception != null)
            {
                _logger.LogError(task.Exception, "Feed worker for {FeedId} crashed.", feed.FeedIdentifier);
            }
        }, TaskScheduler.Default);
        _ = _eventHub.PublishAsync(new FeedStatusEvent(feed.Id, true, isActive));
        return Task.FromResult(true);
    }

    public async Task StopAsync(Guid feedId, bool? isActive = null)
    {
        if (_workers.TryRemove(feedId, out var worker))
        {
            worker.Cancellation.Cancel();
            try
            {
                await worker.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Ignore cancellation/timeout
            }
        }

        await _eventHub.PublishAsync(new FeedStatusEvent(feedId, false, isActive));
    }

    public async Task StartActiveFeedsAsync(IEnumerable<Feed> feeds, CancellationToken cancellationToken)
    {
        foreach (var feed in feeds)
        {
            await StartAsync(feed, cancellationToken, feed.IsActive);
        }
    }

    private record FeedWorker(CancellationTokenSource Cancellation, Task Task);
}
