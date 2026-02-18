namespace Dispatch.Web.Options;

public class TranscriptionOptions
{
    public bool Enabled { get; set; } = true;

    public string Provider { get; set; } = "whisper-cli";

    public string WhisperCliPath { get; set; } = "whisper";

    public string WhisperModel { get; set; } = "base.en";

    public string Language { get; set; } = "en";

    public int PollIntervalSeconds { get; set; } = 5;

    public double ExpectedRealtimeFactor { get; set; } = 0.7;

    public int EstimatedBytesPerSecond { get; set; } = 32000;
}
