using FeedDiscovery;

namespace Dispatch.Web.Models;

public record AddFeedRequest(string BroadcastifyUrl, string? Name);

public record FeedDto(
    Guid Id,
    string Name,
    string BroadcastifyUrl,
    string StreamUrl,
    string FeedIdentifier,
    bool IsActive,
    bool IsRunning,
    DateTime CreatedUtc,
    DateTime? LastStartedUtc,
    DateTime? LastStoppedUtc);

public record RecordingDto(
    Guid Id,
    Guid FeedId,
    string FilePath,
    DateTime StartUtc,
    DateTime EndUtc,
    double DurationSeconds,
    TranscriptStatus TranscriptStatus,
    double TranscriptProgress,
    int? TranscriptQueuePosition,
    string? TranscriptText,
    string? TranscriptPath,
    string? TranscriptProvider);

public record DiscoveryFeedDto(
    string State,
    string County,
    string FeedName,
    FeedStatus FeedStatus,
    string FeedId,
    string AudioUrl);
