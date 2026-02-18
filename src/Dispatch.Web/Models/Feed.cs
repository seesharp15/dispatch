using System.ComponentModel.DataAnnotations;

namespace Dispatch.Web.Models;

public class Feed
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string BroadcastifyUrl { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string StreamUrl { get; set; } = string.Empty;

    [MaxLength(64)]
    public string FeedIdentifier { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastStartedUtc { get; set; }

    public DateTime? LastStoppedUtc { get; set; }

    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
}
