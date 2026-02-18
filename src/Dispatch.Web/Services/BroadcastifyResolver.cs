using System.Text.RegularExpressions;
using Dispatch.Web.Options;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Services;

public class BroadcastifyResolver
{
    private static readonly Regex FeedIdRegex = new(
        "broadcastify\\.com/(?:webPlayer|listen/feed)/(?<id>\\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FeedIdDigitsRegex = new(@"^(?<id>\d+)$", RegexOptions.Compiled);

    private readonly BroadcastifyOptions _options;

    public BroadcastifyResolver(IOptions<BroadcastifyOptions> options)
    {
        _options = options.Value;
    }

    public bool TryResolve(string input, out string feedId, out string streamUrl)
    {
        feedId = string.Empty;
        streamUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        var match = FeedIdRegex.Match(trimmed);
        if (match.Success)
        {
            feedId = match.Groups["id"].Value;
            streamUrl = BuildStreamUrl(feedId);
            return true;
        }

        if (FeedIdDigitsRegex.IsMatch(trimmed))
        {
            feedId = trimmed;
            streamUrl = BuildStreamUrl(feedId);
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Contains("broadcastify.cdnstream", StringComparison.OrdinalIgnoreCase))
            {
                var lastSegment = uri.AbsolutePath.Trim('/');
                if (!string.IsNullOrWhiteSpace(lastSegment))
                {
                    feedId = lastSegment.Split('/')[^1];
                    streamUrl = trimmed;
                    return true;
                }
            }
        }

        return false;
    }

    private string BuildStreamUrl(string feedId)
        => _options.StreamUrlTemplate.Replace("{feedId}", feedId, StringComparison.OrdinalIgnoreCase);
}
