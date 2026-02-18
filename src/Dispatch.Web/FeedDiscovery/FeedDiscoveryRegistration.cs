using FeedDiscovery.Broadcastify;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FeedDiscovery;

public static class FeedDiscoveryRegistration
{
    public static IServiceCollection AddBroadcastifyFeedDiscovery(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<BroadcastifyOptions>(cfg.GetSection("Broadcastify"));
        services.AddMemoryCache();

        services.AddHttpClient<IFeedDiscoveryService, BroadcastifyFeedDiscoveryService>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
