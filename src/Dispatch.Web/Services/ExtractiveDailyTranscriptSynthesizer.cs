using System.Text.RegularExpressions;
using Dispatch.Web.Models;

namespace Dispatch.Web.Services;

public interface IDailyTranscriptSynthesizer
{
    DailyTranscriptSynthesisResult Synthesize(
        IReadOnlyList<Recording> recordings,
        string feedName,
        DateOnly day);
}

public sealed record DailyTranscriptSynthesisResult(
    int TotalCalls,
    int TranscribedCalls,
    string Summary,
    IReadOnlyList<string> KeyThemes,
    IReadOnlyList<SynthesisCategoryCount> Categories,
    IReadOnlyList<SynthesisHighlight> Highlights);

public sealed record SynthesisCategoryCount(string Category, int Count);

public sealed record SynthesisHighlight(
    Guid RecordingId,
    DateTime StartUtc,
    string Category,
    double Score,
    string Excerpt);

public class ExtractiveDailyTranscriptSynthesizer : IDailyTranscriptSynthesizer
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[\.!\?])\s+", RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new(@"[a-z0-9']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "being", "but",
        "by", "for", "from", "had", "has", "have", "he", "her", "hers", "him",
        "his", "i", "if", "in", "into", "is", "it", "its", "just", "me", "my",
        "of", "on", "or", "our", "out", "she", "so", "that", "the", "their",
        "them", "there", "they", "this", "to", "up", "was", "we", "were", "will",
        "with", "you", "your", "unit", "copy", "dispatch", "received", "advised"
    };

    private static readonly string[] UrgentKeywords =
    {
        "shots fired", "stabbing", "armed", "weapon", "critical", "unresponsive",
        "not breathing", "cardiac", "structure fire", "working fire", "officer down",
        "pursuit", "rollover", "domestic", "robbery", "overdose"
    };

    private static readonly (string Category, string[] Keywords)[] CategoryKeywords =
    {
        ("Violence / High Risk", new[] { "shots fired", "stabbing", "assault", "robbery", "weapon", "armed", "domestic", "fight" }),
        ("Medical", new[] { "medical", "ems", "ambulance", "cardiac", "unresponsive", "overdose", "breathing", "injury" }),
        ("Fire / Rescue", new[] { "fire", "smoke", "alarm", "rescue", "structure", "brush", "hazmat" }),
        ("Traffic / Collision", new[] { "crash", "collision", "accident", "vehicle", "mvc", "dui", "rollover", "roadway" }),
        ("Property / Suspicious", new[] { "burglary", "theft", "suspicious", "trespass", "vandal", "prowler", "loitering" })
    };

    public DailyTranscriptSynthesisResult Synthesize(
        IReadOnlyList<Recording> recordings,
        string feedName,
        DateOnly day)
    {
        var totalCalls = recordings.Count;
        var transcribed = recordings
            .Where(r => !string.IsNullOrWhiteSpace(r.TranscriptText))
            .OrderBy(r => r.StartUtc)
            .Select(r => new CallTranscript(
                r.Id,
                r.StartUtc,
                NormalizeText(r.TranscriptText!),
                DetermineCategory(r.TranscriptText!),
                ComputeUrgency(r.TranscriptText!)))
            .ToList();

        if (transcribed.Count == 0)
        {
            return new DailyTranscriptSynthesisResult(
                totalCalls,
                0,
                $"No completed transcripts were available for {feedName} on {day:yyyy-MM-dd}.",
                Array.Empty<string>(),
                Array.Empty<SynthesisCategoryCount>(),
                Array.Empty<SynthesisHighlight>());
        }

        var termFrequency = BuildTermFrequency(transcribed);
        var keyThemes = termFrequency
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(x => x.Key)
            .ToList();

        var categoryCounts = transcribed
            .GroupBy(x => x.Category)
            .Select(g => new SynthesisCategoryCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Category, StringComparer.Ordinal)
            .ToList();

        var highlights = BuildHighlights(transcribed, termFrequency);

        var topCategories = categoryCounts
            .Where(c => !c.Category.Equals("General Dispatch", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(c => $"{c.Category} ({c.Count})")
            .ToList();

        var topThemeText = keyThemes.Count > 0 ? string.Join(", ", keyThemes.Take(5)) : "no dominant recurring terms";
        var categoryText = topCategories.Count > 0 ? string.Join(", ", topCategories) : "mostly routine dispatch traffic";
        var highPriorityCalls = transcribed.Count(x => x.UrgencyScore >= 2);

        var summary =
            $"Analyzed {transcribed.Count} transcribed calls out of {totalCalls} total captures for {feedName} on {day:yyyy-MM-dd}. " +
            $"Common themes: {topThemeText}. " +
            $"Most represented incident categories: {categoryText}. " +
            $"{highPriorityCalls} call{(highPriorityCalls == 1 ? "" : "s")} contained higher-priority language and should be reviewed first.";

        return new DailyTranscriptSynthesisResult(
            totalCalls,
            transcribed.Count,
            summary,
            keyThemes,
            categoryCounts,
            highlights);
    }

    private static List<SynthesisHighlight> BuildHighlights(
        IReadOnlyList<CallTranscript> transcripts,
        IReadOnlyDictionary<string, int> termFrequency)
    {
        var highlights = new List<SynthesisHighlight>();

        foreach (var transcript in transcripts)
        {
            var sentences = SplitSentences(transcript.Text);
            if (sentences.Count == 0)
            {
                continue;
            }

            string? bestSentence = null;
            var bestScore = 0.0;
            foreach (var sentence in sentences)
            {
                var score = ScoreSentence(sentence, transcript, termFrequency);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSentence = sentence;
                }
            }

            if (string.IsNullOrWhiteSpace(bestSentence))
            {
                continue;
            }

            highlights.Add(new SynthesisHighlight(
                transcript.RecordingId,
                transcript.StartUtc,
                transcript.Category,
                Math.Round(bestScore, 2),
                Truncate(bestSentence, 220)));
        }

        return highlights
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.StartUtc)
            .Take(10)
            .ToList();
    }

    private static string DetermineCategory(string transcript)
    {
        var text = transcript.ToLowerInvariant();
        foreach (var (category, keywords) in CategoryKeywords)
        {
            if (keywords.Any(text.Contains))
            {
                return category;
            }
        }

        return "General Dispatch";
    }

    private static int ComputeUrgency(string transcript)
    {
        var text = transcript.ToLowerInvariant();
        return UrgentKeywords.Count(text.Contains);
    }

    private static Dictionary<string, int> BuildTermFrequency(IReadOnlyList<CallTranscript> transcripts)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var transcript in transcripts)
        {
            foreach (var token in Tokenize(transcript.Text))
            {
                if (StopWords.Contains(token) || token.Length < 3)
                {
                    continue;
                }

                map[token] = map.TryGetValue(token, out var count) ? count + 1 : 1;
            }
        }

        return map;
    }

    private static double ScoreSentence(
        string sentence,
        CallTranscript transcript,
        IReadOnlyDictionary<string, int> termFrequency)
    {
        var tokens = Tokenize(sentence)
            .Where(t => t.Length >= 3 && !StopWords.Contains(t))
            .ToList();

        if (tokens.Count == 0)
        {
            return transcript.UrgencyScore * 2;
        }

        double score = tokens.Sum(token => termFrequency.TryGetValue(token, out var count) ? Math.Min(count, 6) : 0);
        score /= Math.Sqrt(tokens.Count);
        score += transcript.UrgencyScore * 2;

        if (!transcript.Category.Equals("General Dispatch", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.75;
        }

        if (tokens.Any(token => token.All(char.IsDigit)))
        {
            score += 0.4;
        }

        return score;
    }

    private static List<string> SplitSentences(string text)
    {
        return SentenceSplitRegex.Split(text)
            .Select(NormalizeText)
            .Where(sentence => sentence.Length >= 24)
            .Take(20)
            .ToList();
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in TokenRegex.Matches(text))
        {
            var token = match.Value.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(token))
            {
                yield return token;
            }
        }
    }

    private static string NormalizeText(string text)
    {
        var normalized = WhitespaceRegex.Replace(text.Trim(), " ");
        return Truncate(normalized, 2800);
    }

    private static string Truncate(string input, int maxLength)
    {
        if (string.IsNullOrEmpty(input) || input.Length <= maxLength)
        {
            return input;
        }

        return input[..maxLength];
    }

    private sealed record CallTranscript(
        Guid RecordingId,
        DateTime StartUtc,
        string Text,
        string Category,
        int UrgencyScore);
}
