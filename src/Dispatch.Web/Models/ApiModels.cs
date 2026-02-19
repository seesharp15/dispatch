using FeedDiscovery;

namespace Dispatch.Web.Models;

public record AddFeedRequest(string BroadcastifyUrl, string? Name);

public record AddLocalFeedRequest(string DeviceId, string? Name, bool StartImmediately = true);

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
    int? TranscriptQueueTotal,
    DateTime? TranscriptStartedUtc,
    string? TranscriptText,
    string? TranscriptPath,
    string? TranscriptProvider,
    bool IsArchived,
    DateTime? ArchivedUtc);

public record BatchRecordingsRequest(IReadOnlyList<Guid> RecordingIds);

public record RecordingDaySummaryDto(
    string Day,
    int TotalCalls);

public record DailySynthesisDto(
    string Day,
    Guid FeedId,
    string FeedName,
    int TotalCalls,
    int TranscribedCalls,
    string Summary,
    IReadOnlyList<string> KeyThemes,
    IReadOnlyList<SynthesisCategoryDto> Categories,
    IReadOnlyList<SynthesisHighlightDto> Highlights);

public record SynthesisCategoryDto(
    string Category,
    int Count);

public record SynthesisHighlightDto(
    Guid RecordingId,
    DateTime StartUtc,
    string Category,
    double Score,
    string Excerpt);

public record DiscoveryFeedDto(
    string State,
    string County,
    string FeedName,
    FeedStatus FeedStatus,
    string FeedId,
    string AudioUrl);

public record LocalAudioDeviceDto(
    string Id,
    string Name,
    string Backend,
    string CaptureKind);
