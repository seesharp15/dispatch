using Microsoft.AspNetCore.Identity;

namespace Dispatch.Web.Models;

public class ApplicationUser : IdentityUser<Guid>
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<UserFeedSubscription> Subscriptions { get; set; } = new List<UserFeedSubscription>();
}
