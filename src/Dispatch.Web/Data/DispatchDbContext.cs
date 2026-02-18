using Dispatch.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Data;

public class DispatchDbContext : DbContext
{
    public DispatchDbContext(DbContextOptions<DispatchDbContext> options) : base(options)
    {
    }

    public DbSet<Feed> Feeds => Set<Feed>();

    public DbSet<Recording> Recordings => Set<Recording>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
    }
}
