using System.Collections.Concurrent;
using Dispatch.Web.Models;
using Microsoft.Extensions.Logging;

namespace Dispatch.Web.Services;

public class FeedCoordinator
{
    private readonly ConcurrentDictionary<Guid, FeedWorker> _workers = new();
    private readonly FeedRecorder _recorder;
    private readonly ILogger<FeedCoordinator> _logger;

    public FeedCoordinator(FeedRecorder recorder, ILogger<FeedCoordinator> logger)
    {
        _recorder = recorder;
        _logger = logger;
    }

    public bool IsRunning(Guid feedId) => _workers.ContainsKey(feedId);

    public Task StartAsync(Feed feed, CancellationToken cancellationToken)
    {
        if (_workers.ContainsKey(feed.Id))
        {
            return Task.CompletedTask;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var worker = new FeedWorker(cts, Task.Run(() => _recorder.RunAsync(feed, cts.Token), cts.Token));
        if (!_workers.TryAdd(feed.Id, worker))
        {
            cts.Cancel();
            return Task.CompletedTask;
        }

        _ = worker.Task.ContinueWith(task =>
        {
            _workers.TryRemove(feed.Id, out _);
            if (task.Exception != null)
            {
                _logger.LogError(task.Exception, "Feed worker for {FeedId} crashed.", feed.FeedIdentifier);
            }
        }, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async Task StopAsync(Guid feedId)
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
    }

    public async Task StartActiveFeedsAsync(IEnumerable<Feed> feeds, CancellationToken cancellationToken)
    {
        foreach (var feed in feeds)
        {
            await StartAsync(feed, cancellationToken);
        }
    }

    private record FeedWorker(CancellationTokenSource Cancellation, Task Task);
}
