using Dispatch.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Data;

public class DispatchDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public DispatchDbContext(DbContextOptions<DispatchDbContext> options) : base(options)
    {
    }

    public DbSet<Feed> Feeds => Set<Feed>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<UserFeedSubscription> UserFeedSubscriptions => Set<UserFeedSubscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity requires this first

        modelBuilder.Entity<Feed>()
            .HasMany(f => f.Recordings)
            .WithOne(r => r.Feed)
            .HasForeignKey(r => r.FeedId);

        modelBuilder.Entity<Feed>()
            .HasIndex(f => f.FeedIdentifier);

        modelBuilder.Entity<Recording>()
            .HasIndex(r => r.FeedId);

        modelBuilder.Entity<Recording>()
            .HasIndex(r => r.TranscriptStatus);

        modelBuilder.Entity<UserFeedSubscription>()
            .HasIndex(s => new { s.UserId, s.FeedId })
            .IsUnique();

        modelBuilder.Entity<UserFeedSubscription>()
            .HasOne(s => s.User)
            .WithMany(u => u.Subscriptions)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserFeedSubscription>()
            .HasOne(s => s.Feed)
            .WithMany(f => f.Subscriptions)
            .HasForeignKey(s => s.FeedId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
