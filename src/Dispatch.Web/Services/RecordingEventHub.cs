using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Dispatch.Web.Services;

public enum RecordingEventType
{
    Created,
    Updated,
    Archived
}

public sealed record RecordingEvent(Guid RecordingId, Guid FeedId, RecordingEventType Type);

public interface IRecordingEventHub
{
    IAsyncEnumerable<RecordingEvent> Subscribe(CancellationToken cancellationToken);

    Task PublishAsync(RecordingEvent recordingEvent);
}

public sealed class RecordingEventHub : IRecordingEventHub
{
    private readonly object _lock = new();
    private readonly List<Channel<RecordingEvent>> _subscribers = new();

    public IAsyncEnumerable<RecordingEvent> Subscribe(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<RecordingEvent>(new UnboundedChannelOptions
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

    public Task PublishAsync(RecordingEvent recordingEvent)
    {
        List<Channel<RecordingEvent>> snapshot;
        lock (_lock)
        {
            snapshot = _subscribers.ToList();
        }

        foreach (var channel in snapshot)
        {
            channel.Writer.TryWrite(recordingEvent);
        }

        return Task.CompletedTask;
    }

    private async IAsyncEnumerable<RecordingEvent> ReadChannel(
        Channel<RecordingEvent> channel,
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
