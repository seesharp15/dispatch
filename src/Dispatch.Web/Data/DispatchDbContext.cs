using Dispatch.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Dispatch.Web.Data;

public class DispatchDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public DispatchDbContext(DbContextOptions<DispatchDbContext> options) : base(options)
    {
    }

    public DbSet<Feed> Feeds => Set<Feed>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<UserFeedSubscription> UserFeedSubscriptions => Set<UserFeedSubscription>();
    public DbSet<UserActiveFeed> UserActiveFeeds => Set<UserActiveFeed>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity requires this first

        // SQLite stores Guid as uppercase text but EF Core queries use lowercase,
        // causing case-sensitive mismatches. Force all Guid properties to lowercase.
        var guidToLower = new ValueConverter<Guid, string>(
            v => v.ToString("D").ToLowerInvariant(),
            v => Guid.Parse(v));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties().Where(p => p.ClrType == typeof(Guid)))
            {
                property.SetValueConverter(guidToLower);
            }
        }

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

        modelBuilder.Entity<UserActiveFeed>()
            .HasIndex(u => new { u.UserId, u.FeedId })
            .IsUnique();

        modelBuilder.Entity<UserActiveFeed>()
            .HasOne(u => u.User)
            .WithMany()
            .HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserActiveFeed>()
            .HasOne(u => u.Feed)
            .WithMany(f => f.ActiveUsers)
            .HasForeignKey(u => u.FeedId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
