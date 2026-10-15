using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private const string EdlAppliedMarker = ".edl-applied";

    /// <summary>
    /// <see cref="PluginConfiguration.WhisperDtwPreset"/> value that disables DTW.
    /// </summary>
    internal const string DtwOff = "off";

    private static readonly string[] DtwPresets =
    {
        "tiny", "tiny.en", "base", "base.en", "small", "small.en", "medium", "medium.en",
        "large.v1", "large.v2", "large.v3", "large.v3.turbo",
    };

    private static readonly Regex ModelQuantizationSuffix = new(@"-(q\d+_\d+|tdrz)$", RegexOptions.Compiled);

    private static readonly string[] TextSubtitleCodecs = { "subrip", "ass", "ssa", "mov_text", "webvtt", "text" };

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

    private const string SkipMarker = ".no-whisper-filter";

    /// <summary>
    /// Returns true if a .no-whisper-filter file exists next to the media file
    /// or in any ancestor directory.
    /// </summary>
    public static bool IsExcluded(string mediaPath)
    {
        var dir = Path.GetDirectoryName(mediaPath);

        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, SkipMarker)))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
            {
                break;
            }

            dir = parent;
        }

        return false;
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
    /// Returns true if transcription is done but EDL filtering hasn't been applied yet.
    /// </summary>
    public static bool NeedsEdlFiltering(string whisperDir)
    {
        if (!Directory.Exists(whisperDir))
        {
            return false;
        }

        var edlPath = Path.Combine(whisperDir, "transcription.edl");
        var edlAppliedPath = Path.Combine(whisperDir, EdlAppliedMarker);

        return File.Exists(edlPath) && !File.Exists(edlAppliedPath);
    }

    /// <summary>
    /// Applies EDL filtering only (no transcription). For already-transcribed files
    /// that need their filtered audio tracks re-created.
    /// </summary>
    public async Task ApplyEdlFilteringAsync(string mediaPath, string outputDir, CancellationToken cancellationToken)
    {
        var edlPath = Path.Combine(outputDir, "transcription.edl");
        var edlAppliedPath = Path.Combine(outputDir, EdlAppliedMarker);

        if (!File.Exists(edlPath))
        {
            return;
        }

        WhisperFileLogger.Info($"Re-applying EDL as filtered audio tracks to: {mediaPath}");
        var filterer = new AudioTrackFilterer(_config.FfmpegPath, _logger);
        await filterer.FilterAsync(mediaPath, edlPath, cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            edlAppliedPath,
            $"Applied at {DateTime.UtcNow:O}",
            cancellationToken).ConfigureAwait(false);
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
            WhisperFileLogger.Info($"Step 1/4: Extracting audio from: {mediaPath}");
            _logger.LogInformation("Extracting audio from: {MediaPath}", mediaPath);
            await ExtractAudioAndSubtitlesAsync(mediaPath, wavPath, outputDir, cancellationToken).ConfigureAwait(false);

            var wavSize = new FileInfo(wavPath).Length / (1024.0 * 1024.0);
            WhisperFileLogger.Info($"Step 2/4: Audio extracted ({wavSize:F0} MB). Running whisper-cli on: {mediaPath}");
            _logger.LogInformation("Running whisper.cpp on: {MediaPath}", mediaPath);

            await RunWhisperAsync(wavPath, outputDir, cancellationToken).ConfigureAwait(false);
            WhisperFileLogger.Info($"Step 3/4: Whisper complete. Generating EDL for: {mediaPath}");

            // Generate EDL mute file from the transcription if mute words are configured.
            var transcriptionPath = Path.Combine(outputDir, "transcription.json");
            var edlPath = EdlGenerator.GetEdlPath(transcriptionPath);

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

            // Apply EDL mute regions as filtered audio tracks in the MKV.
            var edlAppliedPath = Path.Combine(outputDir, EdlAppliedMarker);
            if (File.Exists(edlPath) && !File.Exists(edlAppliedPath))
            {
                WhisperFileLogger.Info($"Step 4/4: Applying EDL as filtered audio tracks to: {mediaPath}");
                var filterer = new AudioTrackFilterer(_config.FfmpegPath, _logger);
                await filterer.FilterAsync(mediaPath, edlPath, cancellationToken).ConfigureAwait(false);

                await File.WriteAllTextAsync(
                    edlAppliedPath,
                    $"Applied at {DateTime.UtcNow:O}",
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

    /// <summary>
    /// Extracts the audio for whisper and, when available, a text subtitle track for
    /// <see cref="SubtitleMuteFinder"/>. Subtitles are optional: failing to get them never fails processing.
    /// </summary>
    private async Task ExtractAudioAndSubtitlesAsync(
        string inputPath,
        string wavPath,
        string outputDir,
        CancellationToken cancellationToken)
    {
        var subtitlesPath = Path.Combine(outputDir, SubtitleMuteFinder.SubtitlesFileName);
        if (File.Exists(subtitlesPath))
        {
            File.Delete(subtitlesPath);
        }

        // Extract audio as 16 kHz mono 16-bit PCM WAV (required by whisper.cpp).
        var audioArguments = $"-i \"{inputPath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{wavPath}\" -y";

        var subtitleStreamIndex = await FindTextSubtitleStreamAsync(inputPath, cancellationToken).ConfigureAwait(false);
        if (subtitleStreamIndex != null)
        {
            // A second output in the same ffmpeg run avoids reading the whole media file twice.
            var exitCode = await RunProcessAsync(
                _config.FfmpegPath,
                $"{audioArguments} -map 0:{subtitleStreamIndex} -c:s srt \"{subtitlesPath}\"",
                "ffmpeg",
                cancellationToken).ConfigureAwait(false);

            if (exitCode == 0)
            {
                return;
            }

            WhisperFileLogger.Info($"Subtitle extraction failed, retrying audio only for: {inputPath}");
            if (File.Exists(subtitlesPath))
            {
                File.Delete(subtitlesPath);
            }
        }

        var audioExitCode = await RunProcessAsync(
            _config.FfmpegPath,
            audioArguments,
            "ffmpeg",
            cancellationToken).ConfigureAwait(false);

        if (audioExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {audioExitCode} for: {inputPath}");
        }

        var sidecarPath = subtitleStreamIndex == null ? FindSidecarSubtitles(inputPath) : null;
        if (sidecarPath != null)
        {
            File.Copy(sidecarPath, subtitlesPath, overwrite: true);
        }
    }

    /// <summary>
    /// Returns the stream index of the best embedded text subtitle track (English, not forced),
    /// or null when there is none. Image-based tracks (DVD, PGS) have no text to read.
    /// </summary>
    private async Task<int?> FindTextSubtitleStreamAsync(string mediaPath, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = AudioTrackFilterer.GetFfprobePath(_config.FfmpegPath),
                Arguments = $"-v quiet -print_format json -show_streams -select_streams s \"{mediaPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var streams = JsonSerializer.Deserialize<SubtitleProbeResult>(stdout)?.Streams ?? new List<SubtitleProbeStream>();

            return streams
                .Where(s => TextSubtitleCodecs.Contains(s.CodecName ?? string.Empty))
                .Where(s => !(s.Tags?.Title ?? string.Empty).Contains("forced", StringComparison.OrdinalIgnoreCase))
                .Where(s => s.Disposition?.Forced != 1)
                .Where(s => IsEnglishOrUnknown(s.Tags?.Language))
                .Select(s => (int?)s.Index)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or JsonException)
        {
            _logger.LogWarning(ex, "Could not probe subtitle streams for: {MediaPath}", mediaPath);
            return null;
        }
    }

    /// <summary>
    /// Finds an external .srt next to the media file using Jellyfin's naming
    /// (Movie.srt, Movie.en.srt, Movie.eng.sdh.srt), skipping forced and non-English ones.
    /// </summary>
    private static string? FindSidecarSubtitles(string mediaPath)
    {
        var dir = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(mediaPath);

        return Directory.EnumerateFiles(dir, "*.srt")
            .Select(path => (Path: path, Flags: Path.GetFileNameWithoutExtension(path)))
            .Where(file => file.Flags.Equals(name, StringComparison.OrdinalIgnoreCase)
                || file.Flags.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
            .Select(file => (file.Path, Flags: file.Flags.Substring(name.Length).ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries)))
            .Where(file => !file.Flags.Contains("forced"))
            .Where(file => file.Flags.Length == 0 || file.Flags.Any(flag => IsEnglishOrUnknown(flag) && flag.Length > 0))
            .OrderBy(file => file.Flags.Length)
            .Select(file => file.Path)
            .FirstOrDefault();
    }

    private static bool IsEnglishOrUnknown(string? language)
    {
        var lang = (language ?? string.Empty).ToLowerInvariant();
        return lang is "" or "en" or "eng" or "english" or "und";
    }

    /// <summary>
    /// Resolves the whisper-cli --dtw preset. An empty setting means auto: derive it from the model
    /// file name (ggml-large-v3-turbo-q5_0.bin is "large.v3.turbo"). Returns null when DTW is off
    /// or the model has no known preset, since whisper-cli exits on an unknown preset.
    /// </summary>
    internal static string? GetDtwPreset(PluginConfiguration config)
    {
        var setting = config.WhisperDtwPreset.Trim();
        if (setting.Equals(DtwOff, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var preset = setting.Length > 0
            ? setting
            : ModelQuantizationSuffix.Replace(
                Path.GetFileNameWithoutExtension(config.WhisperModel.Trim()).ToLowerInvariant(),
                string.Empty)
                .Replace("ggml-", string.Empty, StringComparison.Ordinal)
                .Replace('-', '.');

        return DtwPresets.Contains(preset) ? preset : null;
    }

    private async Task RunWhisperAsync(string wavPath, string outputDir, CancellationToken cancellationToken)
    {
        // whisper.cpp writes <output-file>.json when --output-json-full is used.
        var outputFileBase = Path.Combine(outputDir, "transcription");

        // whisper.cpp silently skips DTW when flash attention is on, which is its default.
        var dtwPreset = GetDtwPreset(_config);
        var dtwArguments = dtwPreset == null ? string.Empty : $"--dtw {dtwPreset} --no-flash-attn ";

        var exitCode = await RunProcessAsync(
            _config.WhisperCppPath,
            $"--model {_config.WhisperModel} {dtwArguments}--output-json-full --output-file \"{outputFileBase}\" \"{wavPath}\"",
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

    private sealed class SubtitleProbeResult
    {
        [JsonPropertyName("streams")]
        public List<SubtitleProbeStream>? Streams { get; set; }
    }

    private sealed class SubtitleProbeStream
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("codec_name")]
        public string? CodecName { get; set; }

        [JsonPropertyName("disposition")]
        public SubtitleProbeDisposition? Disposition { get; set; }

        [JsonPropertyName("tags")]
        public SubtitleProbeTags? Tags { get; set; }
    }

    private sealed class SubtitleProbeDisposition
    {
        [JsonPropertyName("forced")]
        public int Forced { get; set; }
    }

    private sealed class SubtitleProbeTags
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}
