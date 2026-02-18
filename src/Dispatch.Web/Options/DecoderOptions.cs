namespace Dispatch.Web.Options;

public class DecoderOptions
{
    public string FfmpegPath { get; set; } = "ffmpeg";

    public int SampleRate { get; set; } = 16000;

    public int Channels { get; set; } = 1;

    public int FrameMilliseconds { get; set; } = 250;

    public bool EnableReconnect { get; set; } = true;

    public int ReconnectDelaySeconds { get; set; } = 2;
}
