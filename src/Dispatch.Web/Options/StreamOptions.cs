namespace Dispatch.Web.Options;

public class StreamOptions
{
    public int ReconnectDelaySeconds { get; set; } = 5;

    public int HttpTimeoutSeconds { get; set; } = 30;
}
