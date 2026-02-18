namespace FeedDiscovery.Broadcastify;

public sealed class BroadcastifyOptions
{
    public string BaseUrl { get; set; } = "https://www.broadcastify.com";
    public string AudioBaseUrl { get; set; } = "https://broadcastify.cdnstream1.com";
    public int FeedCacheDays { get; set; } = 30;

    public Dictionary<string, int> StateIdMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
