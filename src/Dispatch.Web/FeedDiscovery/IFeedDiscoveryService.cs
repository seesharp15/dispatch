namespace FeedDiscovery;

public interface IFeedDiscoveryService
{
    Task<IReadOnlyList<AudioFeed>> GetFeedsAsync(
        string stateName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AudioFeed>> GetFeedsAsync(
        string stateName,
        string countyName,
        CancellationToken cancellationToken = default);
}
