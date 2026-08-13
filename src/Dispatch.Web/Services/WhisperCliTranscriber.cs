using System.Diagnostics;
using Dispatch.Web.Options;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Services;

public class WhisperCliTranscriber : ITranscriber
{
    private readonly TranscriptionOptions _options;
    private readonly ChildProcessRegistry _processRegistry;
    private readonly ILogger<WhisperCliTranscriber> _logger;

    public WhisperCliTranscriber(IOptions<TranscriptionOptions> options, ChildProcessRegistry processRegistry, ILogger<WhisperCliTranscriber> logger)
    {
        _options = options.Value;
        _processRegistry = processRegistry;
        _logger = logger;
    }

    public async Task<TranscriptResult?> TranscribeAsync(string filePath, CancellationToken cancellationToken)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "dispatch-transcripts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = _options.WhisperCliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_options.WhisperModel);
        psi.ArgumentList.Add("--language");
        psi.ArgumentList.Add(_options.Language);
        psi.ArgumentList.Add("--output_format");
        psi.ArgumentList.Add("txt");
        psi.ArgumentList.Add("--output_dir");
        psi.ArgumentList.Add(outputDir);

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start whisper CLI process.");
        }

        _processRegistry.Register(process);
        try
        {
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                throw;
            }

            var stdout = await stdOutTask;
            var stderr = await stdErrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Whisper CLI failed: {Error}", stderr);
                throw new InvalidOperationException($"Whisper CLI failed with exit code {process.ExitCode}.");
            }

            var transcriptPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(filePath) + ".txt");
            if (!File.Exists(transcriptPath))
            {
                _logger.LogWarning("Whisper CLI did not create transcript at {Path}.", transcriptPath);
                return null;
            }

            var text = await File.ReadAllTextAsync(transcriptPath, cancellationToken);
            return new TranscriptResult(text.Trim(), "whisper-cli", transcriptPath);
        }
        finally
        {
            _processRegistry.Unregister(process);
        }
    }
}
