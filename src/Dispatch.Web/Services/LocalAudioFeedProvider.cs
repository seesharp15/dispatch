using Dispatch.Web.Options;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dispatch.Web.Services;

public sealed record LocalAudioDevice(
    string Id,
    string Name,
    string Backend,
    string Input,
    string CaptureKind);

public interface ILocalAudioFeedProvider
{
    Task<IReadOnlyList<LocalAudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    string BuildStreamUrl(LocalAudioDevice device);

    string BuildFeedIdentifier(LocalAudioDevice device);
}

public sealed class LocalAudioFeedProvider : ILocalAudioFeedProvider
{
    private static readonly Regex AvFoundationAudioLineRegex = new(@"\[(?<index>\d+)\]\s+(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex DshowAudioLineRegex = new("\\[(?<scope>dshow.*?)\\]\\s+\"(?<name>.+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] OutputKeywords =
    {
        "loopback",
        "blackhole",
        "soundflower",
        "stereo mix",
        "what u hear",
        "monitor",
        "system audio",
        "output"
    };

    private readonly DecoderOptions _decoderOptions;
    private readonly ILogger<LocalAudioFeedProvider> _logger;

    public LocalAudioFeedProvider(IOptions<DecoderOptions> decoderOptions, ILogger<LocalAudioFeedProvider> logger)
    {
        _decoderOptions = decoderOptions.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LocalAudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return await GetMacAudioDevicesAsync(cancellationToken);
            }

            if (OperatingSystem.IsWindows())
            {
                return await GetWindowsAudioDevicesAsync(cancellationToken);
            }

            if (OperatingSystem.IsLinux())
            {
                return GetLinuxFallbackDevices();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate local audio devices.");
        }

        return Array.Empty<LocalAudioDevice>();
    }

    public string BuildStreamUrl(LocalAudioDevice device)
        => LocalFeedUri.Build(device.Backend, device.Input);

    public string BuildFeedIdentifier(LocalAudioDevice device)
    {
        var key = $"{device.Backend}|{device.Input}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return $"local-{hash[..12]}";
    }

    private async Task<IReadOnlyList<LocalAudioDevice>> GetMacAudioDevicesAsync(CancellationToken cancellationToken)
    {
        var stderr = await ProbeFfmpegAsync(
            ["-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", ""],
            cancellationToken);

        var devices = new List<LocalAudioDevice>();
        var lines = stderr.Split('\n');
        var inAudioSection = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Contains("AVFoundation audio devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = true;
                continue;
            }

            if (line.Contains("AVFoundation video devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = false;
                continue;
            }

            if (!inAudioSection)
            {
                continue;
            }

            var match = AvFoundationAudioLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var index = match.Groups["index"].Value;
            var name = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            devices.Add(new LocalAudioDevice(
                Id: $"avfoundation:{index}",
                Name: name,
                Backend: "avfoundation",
                Input: $":{index}",
                CaptureKind: InferCaptureKind(name)));
        }

        return devices;
    }

    private async Task<IReadOnlyList<LocalAudioDevice>> GetWindowsAudioDevicesAsync(CancellationToken cancellationToken)
    {
        var stderr = await ProbeFfmpegAsync(
            ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"],
            cancellationToken);

        var devices = new List<LocalAudioDevice>();
        var lines = stderr.Split('\n');
        var inAudioSection = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = true;
                continue;
            }

            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = false;
                continue;
            }

            if (!inAudioSection)
            {
                continue;
            }

            var match = DshowAudioLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            devices.Add(new LocalAudioDevice(
                Id: $"dshow:{ShortHash(name)}",
                Name: name,
                Backend: "dshow",
                Input: $"audio={name}",
                CaptureKind: InferCaptureKind(name)));
        }

        return devices;
    }

    private static IReadOnlyList<LocalAudioDevice> GetLinuxFallbackDevices()
    {
        return
        [
            new LocalAudioDevice(
                Id: "pulse:default",
                Name: "PulseAudio Default Input",
                Backend: "pulse",
                Input: "default",
                CaptureKind: "Input"),
            new LocalAudioDevice(
                Id: "alsa:default",
                Name: "ALSA Default Input",
                Backend: "alsa",
                Input: "default",
                CaptureKind: "Input")
        ];
    }

    private async Task<string> ProbeFfmpegAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        var psi = new ProcessStartInfo
        {
            FileName = _decoderOptions.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Unable to start ffmpeg for local device discovery.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }
            }
            throw;
        }

        // Ensure both streams are fully consumed.
        await stdoutTask;
        return await stderrTask;
    }

    private static string InferCaptureKind(string name)
    {
        foreach (var keyword in OutputKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return "Output";
            }
        }

        return "Input";
    }

    private static string ShortHash(string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return hash[..12];
    }
}
