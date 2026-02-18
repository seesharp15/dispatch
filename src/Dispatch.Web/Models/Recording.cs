using System.ComponentModel.DataAnnotations;

namespace Dispatch.Web.Models;

public class Recording
{
    [Key]
    public Guid Id { get; set; }

    public Guid FeedId { get; set; }

    public Feed? Feed { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public double DurationSeconds { get; set; }

    [MaxLength(2048)]
    public string FilePath { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? TranscriptPath { get; set; }

    public TranscriptStatus TranscriptStatus { get; set; } = TranscriptStatus.Pending;

    [MaxLength(128)]
    public string? TranscriptProvider { get; set; }

    public DateTime? TranscriptStartedUtc { get; set; }

    public string? TranscriptText { get; set; }

    [MaxLength(1024)]
    public string? Error { get; set; }

    public bool IsArchived { get; set; }

    public DateTime? ArchivedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? TranscribedUtc { get; set; }
}
