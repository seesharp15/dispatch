namespace FeedDiscovery;

public enum FeedStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2
}

public sealed record AudioFeed(
    string State,
    string County,
    string FeedName,
    FeedStatus FeedStatus,
    Uri AudioSource
);
