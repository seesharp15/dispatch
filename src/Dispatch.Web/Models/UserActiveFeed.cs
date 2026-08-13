using System.ComponentModel.DataAnnotations;

namespace Dispatch.Web.Models;

public class UserActiveFeed
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public Guid FeedId { get; set; }
    public Feed Feed { get; set; } = null!;

    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;
}
