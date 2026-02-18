using Dispatch.Web.Data;
using Dispatch.Web.Models;
using Dispatch.Web.Options;
using Dispatch.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Workers;

public class TranscriptionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITranscriber _transcriber;
    private readonly TranscriptionOptions _options;
    private readonly ILogger<TranscriptionWorker> _logger;

    public TranscriptionWorker(
        IServiceScopeFactory scopeFactory,
        ITranscriber transcriber,
        IOptions<TranscriptionOptions> options,
        ILogger<TranscriptionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _transcriber = transcriber;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transcription worker loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessNextAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
        var recording = await db.Recordings
            .Where(r => r.TranscriptStatus == TranscriptStatus.Pending && !r.IsArchived)
            .OrderBy(r => r.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (recording == null)
        {
            return;
        }

        recording.TranscriptStatus = TranscriptStatus.Processing;
        recording.TranscriptStartedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            if (!File.Exists(recording.FilePath))
            {
                recording.TranscriptStatus = TranscriptStatus.Failed;
                recording.Error = "Audio file not found.";
                recording.TranscriptStartedUtc ??= DateTime.UtcNow;
                recording.TranscribedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var result = await _transcriber.TranscribeAsync(recording.FilePath, cancellationToken);
            if (result == null)
            {
            recording.TranscriptStatus = TranscriptStatus.Skipped;
            recording.TranscriptProvider = null;
            recording.TranscriptStartedUtc ??= DateTime.UtcNow;
            recording.TranscribedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

            var transcriptPath = recording.FilePath + ".txt";
            await File.WriteAllTextAsync(transcriptPath, result.Text, cancellationToken);

            recording.TranscriptStatus = TranscriptStatus.Complete;
            recording.TranscriptProvider = result.Provider;
            recording.TranscriptPath = transcriptPath;
            recording.TranscriptText = result.Text;
            recording.TranscriptStartedUtc ??= DateTime.UtcNow;
            recording.TranscribedUtc = DateTime.UtcNow;
            recording.Error = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            recording.TranscriptStatus = TranscriptStatus.Failed;
            recording.Error = ex.Message;
            recording.TranscriptStartedUtc ??= DateTime.UtcNow;
            recording.TranscribedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(ex, "Failed to transcribe recording {RecordingId}.", recording.Id);
        }
    }
}
