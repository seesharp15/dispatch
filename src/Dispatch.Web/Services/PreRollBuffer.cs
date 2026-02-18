namespace Dispatch.Web.Services;

public class PreRollBuffer
{
    private readonly Queue<byte[]> _buffers = new();
    private readonly int _capacityBytes;
    private int _totalBytes;

    public PreRollBuffer(int capacityBytes)
    {
        _capacityBytes = Math.Max(capacityBytes, 0);
    }

    public int TotalBytes => _totalBytes;

    public void Add(ReadOnlySpan<byte> data)
    {
        if (_capacityBytes <= 0 || data.Length == 0)
        {
            return;
        }

        var copy = data.ToArray();
        _buffers.Enqueue(copy);
        _totalBytes += copy.Length;

        while (_totalBytes > _capacityBytes && _buffers.Count > 0)
        {
            var removed = _buffers.Dequeue();
            _totalBytes -= removed.Length;
        }
    }

    public IEnumerable<byte[]> Drain()
    {
        while (_buffers.Count > 0)
        {
            yield return _buffers.Dequeue();
        }

        _totalBytes = 0;
    }
}
