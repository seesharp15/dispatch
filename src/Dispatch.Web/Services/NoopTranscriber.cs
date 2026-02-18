namespace Dispatch.Web.Services;

public class NoopTranscriber : ITranscriber
{
    public Task<TranscriptResult?> TranscribeAsync(string filePath, CancellationToken cancellationToken)
        => Task.FromResult<TranscriptResult?>(null);
}
