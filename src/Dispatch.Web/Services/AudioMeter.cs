using NAudio.Wave;

namespace Dispatch.Web.Services;

public static class AudioMeter
{
    public static double ComputeDb(ReadOnlySpan<byte> pcmBuffer, int bytes, WaveFormat format)
    {
        if (bytes <= 0)
        {
            return -100;
        }

        if (format.BitsPerSample != 16)
        {
            throw new NotSupportedException($"Only 16-bit PCM is supported. Got {format.BitsPerSample}.");
        }

        var sampleCount = bytes / 2;
        if (sampleCount == 0)
        {
            return -100;
        }

        double sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(pcmBuffer.Slice(i * 2, 2));
            var normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        var db = 20 * Math.Log10(rms + 1e-9);
        return db;
    }
}
