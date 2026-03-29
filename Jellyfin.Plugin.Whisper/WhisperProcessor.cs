using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Handles audio extraction and whisper.cpp processing for a single media file.
/// </summary>
public class WhisperProcessor
{
    private const string CompleteMarker = ".complete";

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperProcessor"/> class.
    /// </summary>
    public WhisperProcessor(PluginConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the whisper output directory exists and processing completed successfully.
    /// </summary>
    public static bool IsAlreadyProcessed(string whisperDir)
    {
        return Directory.Exists(whisperDir)
            && File.Exists(Path.Combine(whisperDir, CompleteMarker));
    }

    /// <summary>
    /// Computes the .whisper output directory path for a given media file.
    /// </summary>
    public static string GetWhisperDirectory(string mediaPath)
    {
        var dir = Path.GetDirectoryName(mediaPath)
            ?? throw new InvalidOperationException($"Could not determine directory for: {mediaPath}");
        var name = Path.GetFileNameWithoutExtension(mediaPath);
        return Path.Combine(dir, $"{name}.whisper");
    }

    /// <summary>
    /// Processes a media file: extracts audio, runs whisper.cpp, stores output in the .whisper directory.
    /// </summary>
    public async Task ProcessAsync(string mediaPath, string outputDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDir);

        var wavPath = Path.Combine(outputDir, "audio.wav");
        var succeeded = false;

        try
        {
            _logger.LogInformation("Extracting audio from: {MediaPath}", mediaPath);
            await ExtractAudioAsync(mediaPath, wavPath, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Running whisper.cpp on: {MediaPath}", mediaPath);
            await RunWhisperAsync(wavPath, outputDir, cancellationToken).ConfigureAwait(false);

            // Generate EDL mute file from the transcription if mute words are configured.
            var transcriptionPath = Path.Combine(outputDir, "transcription.json");
            if (File.Exists(transcriptionPath) && !string.IsNullOrWhiteSpace(_config.MuteWords))
            {
                _logger.LogInformation("Generating EDL for: {MediaPath}", mediaPath);
                var edlGenerator = new EdlGenerator(_logger);
                await edlGenerator.GenerateAsync(
                    transcriptionPath,
                    mediaPath,
                    _config.MuteWords,
                    _config.EdlBufferMs,
                    cancellationToken).ConfigureAwait(false);
            }

            // Write completion marker so we know this file was fully processed.
            await File.WriteAllTextAsync(
                Path.Combine(outputDir, CompleteMarker),
                $"Completed at {DateTime.UtcNow:O}",
                cancellationToken).ConfigureAwait(false);

            succeeded = true;
            _logger.LogInformation("Whisper processing complete for: {MediaPath}", mediaPath);
        }
        finally
        {
            // Always clean up the intermediate WAV file.
            if (File.Exists(wavPath))
            {
                try
                {
                    File.Delete(wavPath);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temp WAV: {WavPath}", wavPath);
                }
            }

            // Remove the .whisper directory if processing failed so it doesn't
            // litter the media folder with empty directories.
            if (!succeeded && Directory.Exists(outputDir))
            {
                try
                {
                    Directory.Delete(outputDir, recursive: true);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up failed whisper dir: {Path}", outputDir);
                }
            }
        }
    }

    private async Task ExtractAudioAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        // Extract audio as 16 kHz mono 16-bit PCM WAV (required by whisper.cpp).
        var exitCode = await RunProcessAsync(
            _config.FfmpegPath,
            $"-i \"{inputPath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{outputPath}\" -y",
            "ffmpeg",
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {exitCode} for: {inputPath}");
        }
    }

    private async Task RunWhisperAsync(string wavPath, string outputDir, CancellationToken cancellationToken)
    {
        // whisper.cpp writes <output-file>.json when --output-json-full is used.
        var outputFileBase = Path.Combine(outputDir, "transcription");

        var exitCode = await RunProcessAsync(
            _config.WhisperCppPath,
            $"--model {_config.WhisperModel} --output-json-full --output-file \"{outputFileBase}\" \"{wavPath}\"",
            "whisper-cpp",
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"whisper-cpp exited with code {exitCode} for: {wavPath}");
        }

        // Verify the output file was created.
        var expectedOutput = outputFileBase + ".json";
        if (!File.Exists(expectedOutput))
        {
            throw new InvalidOperationException(
                $"whisper-cpp did not produce expected output file: {expectedOutput}");
        }
    }

    private async Task<int> RunProcessAsync(
        string fileName,
        string arguments,
        string processName,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        // Kill the process if cancellation is requested (e.g. server shutdown).
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have already exited.
            }
        });

        // Read stdout/stderr asynchronously to prevent buffer deadlocks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "{ProcessName} failed (exit code {ExitCode}). stderr:\n{Stderr}",
                processName,
                process.ExitCode,
                stderr);
            WhisperFileLogger.Error($"{processName} failed (exit code {process.ExitCode}). stderr: {stderr}");
        }

        return process.ExitCode;
    }
}
