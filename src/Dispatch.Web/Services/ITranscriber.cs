namespace Dispatch.Web.Services;

public record TranscriptResult(string Text, string Provider, string? OutputPath = null);

public interface ITranscriber
{
    Task<TranscriptResult?> TranscribeAsync(string filePath, CancellationToken cancellationToken);
}
