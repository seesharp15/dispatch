using Dispatch.Web.Data;
using Dispatch.Web.Models;
using Dispatch.Web.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using System.Diagnostics;

namespace Dispatch.Web.Services;

public class FeedRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FeedRecorder> _logger;
    private readonly StorageOptions _storageOptions;
    private readonly SegmentationOptions _segmentationOptions;
    private readonly StreamOptions _streamOptions;
    private readonly DecoderOptions _decoderOptions;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IRecordingEventHub _eventHub;

    public FeedRecorder(
        IServiceScopeFactory scopeFactory,
        IOptions<StorageOptions> storageOptions,
        IOptions<SegmentationOptions> segmentationOptions,
        IOptions<StreamOptions> streamOptions,
        IOptions<DecoderOptions> decoderOptions,
        IHostEnvironment hostEnvironment,
        IRecordingEventHub eventHub,
        ILogger<FeedRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _storageOptions = storageOptions.Value;
        _segmentationOptions = segmentationOptions.Value;
        _streamOptions = streamOptions.Value;
        _decoderOptions = decoderOptions.Value;
        _hostEnvironment = hostEnvironment;
        _eventHub = eventHub;
    }

    public async Task RunAsync(Feed feed, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StreamAndRecordAsync(feed, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stream error for feed {FeedId}. Reconnecting in {Delay}s.", feed.FeedIdentifier, _streamOptions.ReconnectDelaySeconds);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_streamOptions.ReconnectDelaySeconds), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task StreamAndRecordAsync(Feed feed, CancellationToken cancellationToken)
    {
        using var process = StartFfmpeg(feed.StreamUrl);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await using var stream = process.StandardOutput.BaseStream;
        var waveFormat = new WaveFormat(_decoderOptions.SampleRate, 16, _decoderOptions.Channels);
        var bytesPerSecond = waveFormat.AverageBytesPerSecond;
        var frameBytes = Math.Max(bytesPerSecond * _decoderOptions.FrameMilliseconds / 1000, waveFormat.BlockAlign);
        var buffer = new byte[frameBytes];
        var segmenter = new AudioSegmenter(_segmentationOptions);
        var preRollBytes = (int)(bytesPerSecond * _segmentationOptions.PreRollSeconds);
        var preRoll = new PreRollBuffer(preRollBytes);
        WaveFileWriter? writer = null;
        string? currentFilePath = null;
        var segmentStartUtc = DateTime.UtcNow;
        var segmentSeconds = 0.0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            var aligned = read - (read % waveFormat.BlockAlign);
            if (aligned <= 0)
            {
                continue;
            }

            var span = new ReadOnlySpan<byte>(buffer, 0, aligned);
            var frameSeconds = aligned / (double)bytesPerSecond;
            var db = AudioMeter.ComputeDb(span, aligned, waveFormat);

            var wasRecording = segmenter.IsRecording;
            var segmentEvent = segmenter.ProcessFrame(db, frameSeconds);

            if (!wasRecording && segmentEvent != SegmentEvent.Started)
            {
                preRoll.Add(span);
            }

            if (segmentEvent == SegmentEvent.Started)
            {
                segmentSeconds = 0;
                currentFilePath = BuildRecordingPath(feed);
                Directory.CreateDirectory(Path.GetDirectoryName(currentFilePath)!);
                writer = new WaveFileWriter(currentFilePath, waveFormat);
                var preRollSeconds = preRoll.TotalBytes / (double)waveFormat.AverageBytesPerSecond;
                foreach (var chunk in preRoll.Drain())
                {
                    writer.Write(chunk, 0, chunk.Length);
                }
                segmentStartUtc = DateTime.UtcNow - TimeSpan.FromSeconds(preRollSeconds);
                segmentSeconds += preRollSeconds;
            }

            if (wasRecording || segmentEvent == SegmentEvent.Started)
            {
                if (writer != null)
                {
                    writer.Write(buffer, 0, aligned);
                }
                segmentSeconds += frameSeconds;
            }

            if (segmentEvent == SegmentEvent.Stopped)
            {
                writer?.Dispose();
                writer = null;

                if (!string.IsNullOrWhiteSpace(currentFilePath))
                {
                    var endUtc = DateTime.UtcNow;
                    await SaveRecordingAsync(feed, currentFilePath, segmentStartUtc, endUtc, segmentSeconds, cancellationToken);
                }

                currentFilePath = null;
            }
        }

        if (writer != null && !string.IsNullOrWhiteSpace(currentFilePath))
        {
            writer.Dispose();
            var endUtc = DateTime.UtcNow;
            await SaveRecordingAsync(feed, currentFilePath, segmentStartUtc, endUtc, segmentSeconds, cancellationToken);
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogDebug("FFmpeg stderr: {Stderr}", stderr);
        }
    }

    private Process StartFfmpeg(string streamUrl)
    {
        var isLocalSource = LocalFeedUri.TryParse(streamUrl, out var localBackend, out var localInput);

        var psi = new ProcessStartInfo
        {
            FileName = _decoderOptions.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");

        if (!isLocalSource && _decoderOptions.EnableReconnect)
        {
            psi.ArgumentList.Add("-reconnect");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_streamed");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_delay_max");
            psi.ArgumentList.Add(_decoderOptions.ReconnectDelaySeconds.ToString());
        }

        if (isLocalSource)
        {
            var format = localBackend.ToLowerInvariant();
            switch (format)
            {
                case "avfoundation":
                case "dshow":
                case "pulse":
                case "alsa":
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add(format);
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(localInput);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported local capture backend '{localBackend}'.");
            }
        }
        else
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(streamUrl);
        }

        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(_decoderOptions.Channels.ToString());
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(_decoderOptions.SampleRate.ToString());
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("pipe:1");

        var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Unable to start ffmpeg process.");
        }

        return process;
    }

    private string BuildRecordingPath(Feed feed)
    {
        var dateFolder = DateTime.UtcNow.ToString("yyyyMMdd");
        var fileName = $"{DateTime.UtcNow:HHmmss}_segment.wav";
        var basePath = _storageOptions.RecordingsPath;
        if (!Path.IsPathRooted(basePath))
        {
            basePath = Path.GetFullPath(Path.Combine(_hostEnvironment.ContentRootPath, basePath));
        }

        return Path.Combine(basePath, feed.FeedIdentifier, dateFolder, fileName);
    }

    private async Task SaveRecordingAsync(Feed feed, string filePath, DateTime startUtc, DateTime endUtc, double durationSeconds, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();

        var exists = await db.Recordings.AnyAsync(r => r.FilePath == filePath, cancellationToken);
        if (exists)
        {
            return;
        }

        var recordingId = Guid.NewGuid();
        db.Recordings.Add(new Recording
        {
            Id = recordingId,
            FeedId = feed.Id,
            StartUtc = startUtc,
            EndUtc = endUtc,
            DurationSeconds = durationSeconds,
            FilePath = filePath,
            TranscriptStatus = TranscriptStatus.Pending
        });

        await db.SaveChangesAsync(cancellationToken);
        await _eventHub.PublishAsync(new RecordingEvent(recordingId, feed.Id, RecordingEventType.Created));
        _logger.LogInformation("Saved recording {FilePath} ({Duration}s) for feed {FeedId}.", filePath, durationSeconds, feed.FeedIdentifier);
    }
}
