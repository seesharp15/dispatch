using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Dispatch.Web.Services;

public sealed record FeedStatusEvent(Guid FeedId, bool IsRunning, bool? IsActive);

public interface IFeedEventHub
{
    IAsyncEnumerable<FeedStatusEvent> Subscribe(CancellationToken cancellationToken);

    Task PublishAsync(FeedStatusEvent statusEvent);
}

public sealed class FeedEventHub : IFeedEventHub
{
    private readonly object _lock = new();
    private readonly List<Channel<FeedStatusEvent>> _subscribers = new();

    public IAsyncEnumerable<FeedStatusEvent> Subscribe(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<FeedStatusEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        return ReadChannel(channel, cancellationToken);
    }

    public Task PublishAsync(FeedStatusEvent statusEvent)
    {
        List<Channel<FeedStatusEvent>> snapshot;
        lock (_lock)
        {
            snapshot = _subscribers.ToList();
        }

        foreach (var channel in snapshot)
        {
            channel.Writer.TryWrite(statusEvent);
        }

        return Task.CompletedTask;
    }

    private async IAsyncEnumerable<FeedStatusEvent> ReadChannel(
        Channel<FeedStatusEvent> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
            }
        }
    }
}
