using System.Net;
using System.Text.RegularExpressions;
using FeedDiscovery;
using HtmlAgilityPack;
using Microsoft.Extensions.Options;

namespace FeedDiscovery.Broadcastify;

public sealed class BroadcastifyFeedDiscoveryService : IFeedDiscoveryService
{
    private readonly HttpClient _http;
    private readonly BroadcastifyOptions _opt;

    public BroadcastifyFeedDiscoveryService(HttpClient http, IOptions<BroadcastifyOptions> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _opt = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<IReadOnlyList<AudioFeed>> GetFeedsAsync(
        string stateName,
        CancellationToken cancellationToken = default)
        => GetFeedsInternalAsync(stateName, countyName: null, cancellationToken);

    public Task<IReadOnlyList<AudioFeed>> GetFeedsAsync(
        string stateName,
        string countyName,
        CancellationToken cancellationToken = default)
        => GetFeedsInternalAsync(stateName, countyName, cancellationToken);

    private async Task<IReadOnlyList<AudioFeed>> GetFeedsInternalAsync(
        string stateName,
        string? countyName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stateName))
        {
            throw new ArgumentException("State name is required.", nameof(stateName));
        }

        var normalizedState = NormalizeKey(stateName);
        if (!_opt.StateIdMap.TryGetValue(normalizedState, out var stateId))
        {
            if (!_opt.StateIdMap.TryGetValue(stateName.Trim(), out stateId))
            {
                throw new KeyNotFoundException($"Unknown state '{stateName}'.");
            }
        }

        var stateUrl = $"{_opt.BaseUrl.TrimEnd('/')}/listen/stid/{stateId}";
        var html = await GetStringAsync(stateUrl, ct).ConfigureAwait(false);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var feeds = new List<AudioFeed>(capacity: 512);
        var stateDisplay = stateName.Trim();

        foreach (var feedRow in ParseAreawideFeeds(doc, stateDisplay))
        {
            feeds.Add(feedRow);
        }

        foreach (var feedRow in ParseAllFeedsTable(doc, stateDisplay))
        {
            feeds.Add(feedRow);
        }

        if (!string.IsNullOrWhiteSpace(countyName))
        {
            var normalizedCounty = NormalizeKey(countyName);
            feeds = feeds
                .Where(f => NormalizeKey(f.County) == normalizedCounty)
                .ToList();
        }

        return feeds;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("FeedDiscoveryService/1.0 (+https://example.local)");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException(
                "Broadcastify returned 403 (blocked). Consider adding cookies, respecting robots/ToS, or using an alternate endpoint.");
        }

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private IEnumerable<AudioFeed> ParseAreawideFeeds(HtmlDocument doc, string stateName)
    {
        var header = doc.DocumentNode
            .SelectSingleNode("//h2[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'live areawide feeds')]" +
                              " | //h3[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'live areawide feeds')]");

        if (header is null)
        {
            yield break;
        }

        var table = header.SelectSingleNode("following::table[1]");
        if (table is null)
        {
            yield break;
        }

        foreach (var tr in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var feedLink = tr.SelectSingleNode(".//a[contains(@href,'/listen/feed/')]");
            if (feedLink is null)
            {
                continue;
            }

            var feedId = TryParseFeedId(feedLink.GetAttributeValue("href", ""));
            if (feedId is null)
            {
                continue;
            }

            var tds = tr.SelectNodes("./td") ?? new HtmlNodeCollection(tr);
            var statusText = tds.Count >= 1 ? Clean(tds[^1].InnerText) : "";

            yield return new AudioFeed(
                State: stateName,
                County: "Statewide",
                FeedName: Clean(feedLink.InnerText),
                FeedStatus: ParseStatus(statusText),
                AudioSource: BuildAudioUri(feedId.Value)
            );
        }
    }

    private IEnumerable<AudioFeed> ParseAllFeedsTable(HtmlDocument doc, string stateName)
    {
        var header = doc.DocumentNode
            .SelectSingleNode("//h2[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'all feeds in the state')]" +
                              " | //h3[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'all feeds in the state')]");

        if (header is null)
        {
            yield break;
        }

        var table = header.SelectSingleNode("following::table[1]");
        if (table is null)
        {
            yield break;
        }

        foreach (var tr in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count < 4)
            {
                continue;
            }

            var countyText = Clean(tds[0].InnerText);
            var feedLink = tds[1].SelectSingleNode(".//a[contains(@href,'/listen/feed/')]");
            if (feedLink is null)
            {
                continue;
            }

            var feedId = TryParseFeedId(feedLink.GetAttributeValue("href", ""));
            if (feedId is null)
            {
                continue;
            }

            var statusText = Clean(tds[^1].InnerText);
            var county = NormalizeKey(countyText) switch
            {
                "statewide" => "Statewide",
                _ => countyText
            };

            yield return new AudioFeed(
                State: stateName,
                County: string.IsNullOrWhiteSpace(county) ? "Statewide" : county,
                FeedName: Clean(feedLink.InnerText),
                FeedStatus: ParseStatus(statusText),
                AudioSource: BuildAudioUri(feedId.Value)
            );
        }
    }

    private Uri BuildAudioUri(int feedId)
        => new($"{_opt.AudioBaseUrl.TrimEnd('/')}/{feedId}");

    private static int? TryParseFeedId(string href)
    {
        var match = Regex.Match(href ?? string.Empty, @"/listen/feed/(?<id>\d+)");
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups["id"].Value, out var id))
        {
            return null;
        }

        return id;
    }

    private static FeedStatus ParseStatus(string statusText)
    {
        var text = NormalizeKey(statusText);
        if (text.Contains("online"))
        {
            return FeedStatus.Online;
        }

        if (text.Contains("offline"))
        {
            return FeedStatus.Offline;
        }

        return FeedStatus.Unknown;
    }

    private static string Clean(string value)
        => WebUtility.HtmlDecode(value ?? string.Empty).Replace("\u00A0", " ").Trim();

    private static string NormalizeKey(string value)
        => Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ").ToLowerInvariant();
}
