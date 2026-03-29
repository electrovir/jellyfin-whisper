using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Creates filtered (muted) copies of English audio tracks in MKV files.
/// Reads EDL mute timestamps and uses ffmpeg to add new audio tracks with
/// volume=0 applied at the muted regions. Original tracks are preserved.
/// </summary>
public class AudioTrackFilterer
{
    private const string FilteredTrackPrefix = "(filtered) ";

    private readonly string _ffmpegPath;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioTrackFilterer"/> class.
    /// </summary>
    public AudioTrackFilterer(string ffmpegPath, ILogger logger)
    {
        _ffmpegPath = ffmpegPath;
        _logger = logger;
    }

    /// <summary>
    /// Adds filtered audio tracks to the media file for each English audio track.
    /// Reads mute regions from the EDL file and applies volume=0 at those timestamps.
    /// </summary>
    public async Task FilterAsync(string mediaPath, string edlPath, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(edlPath))
        {
            WhisperFileLogger.Info($"No EDL file found, skipping audio filtering for: {mediaPath}");
            return;
        }

        var muteRegions = ParseEdl(edlPath);
        if (muteRegions.Count == 0)
        {
            WhisperFileLogger.Info($"EDL has no mute regions, skipping audio filtering for: {mediaPath}");
            return;
        }

        var streams = await ProbeAudioStreamsAsync(mediaPath, cancellationToken).ConfigureAwait(false);
        var englishStreams = streams
            .Where(s => IsEnglishTrack(s))
            .ToList();

        if (englishStreams.Count == 0)
        {
            WhisperFileLogger.Info($"No English audio tracks found, skipping audio filtering for: {mediaPath}");
            return;
        }

        // If filtered tracks already exist, remove them first so we can re-create with updated EDL.
        var existingFilteredStreams = streams
            .Where(s => (s.Title ?? string.Empty).StartsWith(FilteredTrackPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (existingFilteredStreams.Count > 0)
        {
            WhisperFileLogger.Info($"Removing {existingFilteredStreams.Count} existing filtered track(s) from: {mediaPath}");
            await RemoveFilteredTracksAsync(mediaPath, streams, existingFilteredStreams, cancellationToken)
                .ConfigureAwait(false);

            // Re-probe after removal to get updated stream indices.
            streams = await ProbeAudioStreamsAsync(mediaPath, cancellationToken).ConfigureAwait(false);
            englishStreams = streams.Where(s => IsEnglishTrack(s)).ToList();
        }

        var volumeFilter = BuildVolumeFilter(muteRegions);
        var tempPath = mediaPath + ".filtering.tmp";

        try
        {
            var args = BuildFfmpegArgs(mediaPath, tempPath, streams, englishStreams, volumeFilter);
            WhisperFileLogger.Info($"Adding {englishStreams.Count} filtered audio track(s) to: {mediaPath}");

            var exitCode = await RunProcessAsync(_ffmpegPath, args, "ffmpeg-filter", cancellationToken)
                .ConfigureAwait(false);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg audio filtering exited with code {exitCode} for: {mediaPath}");
            }

            // Atomic replace: rename temp over original.
            System.IO.File.Move(tempPath, mediaPath, overwrite: true);
            WhisperFileLogger.Info($"Filtered audio tracks added successfully to: {mediaPath}");
        }
        finally
        {
            // Clean up temp file if it still exists (failed run).
            if (System.IO.File.Exists(tempPath))
            {
                try
                {
                    System.IO.File.Delete(tempPath);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temp file: {Path}", tempPath);
                }
            }
        }
    }

    private async Task RemoveFilteredTracksAsync(
        string mediaPath,
        List<AudioStreamInfo> allStreams,
        List<AudioStreamInfo> filteredStreams,
        CancellationToken cancellationToken)
    {
        var tempPath = mediaPath + ".removing.tmp";
        var filteredIndices = new HashSet<int>(filteredStreams.Select(s => s.Index));

        // Build ffmpeg args that map all streams EXCEPT the filtered ones.
        var args = new List<string>
        {
            "-i",
            $"\"{mediaPath}\"",
        };

        // We need to use ffprobe's full stream list (not just audio) for correct mapping.
        // Map all streams, skip filtered audio by absolute index.
        args.Add("-map");
        args.Add("0");

        foreach (var idx in filteredIndices)
        {
            args.Add($"-map");
            args.Add($"-0:{idx}");
        }

        args.Add("-c");
        args.Add("copy");
        args.Add("-y");
        args.Add($"\"{tempPath}\"");

        try
        {
            var exitCode = await RunProcessAsync(_ffmpegPath, string.Join(" ", args), "ffmpeg-remove-filtered", cancellationToken)
                .ConfigureAwait(false);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg failed to remove filtered tracks (exit code {exitCode}) for: {mediaPath}");
            }

            System.IO.File.Move(tempPath, mediaPath, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                try
                {
                    System.IO.File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best effort cleanup.
                }
            }
        }
    }

    private static List<MuteRegion> ParseEdl(string edlPath)
    {
        var regions = new List<MuteRegion>();

        foreach (var line in System.IO.File.ReadAllLines(edlPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split('\t');
            if (parts.Length >= 3
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                && parts[2] == "1")
            {
                regions.Add(new MuteRegion(start, end));
            }
        }

        return regions;
    }

    private static string BuildVolumeFilter(List<MuteRegion> regions)
    {
        // Chain volume filters: each one mutes a specific time range.
        var filters = regions.Select(r =>
            string.Format(
                CultureInfo.InvariantCulture,
                "volume=enable='between(t,{0:F3},{1:F3})':volume=0",
                r.Start,
                r.End));
        return string.Join(",", filters);
    }

    private static string BuildFfmpegArgs(
        string inputPath,
        string outputPath,
        List<AudioStreamInfo> allStreams,
        List<AudioStreamInfo> englishStreams,
        string volumeFilter)
    {
        // Strategy:
        // -map 0 copies all existing streams (video, audio, subtitles, etc.)
        // For each English audio stream, we add another -map 0:a:N to create the filtered copy.
        // The filtered copies get encoded with AAC and the volume filter applied.
        // Original streams are copied as-is.

        var args = new List<string>
        {
            "-i",
            $"\"{inputPath}\"",
            "-map",
            "0",
        };

        // Map each English audio stream again for the filtered version.
        foreach (var stream in englishStreams)
        {
            args.Add("-map");
            args.Add($"0:{stream.Index}");
        }

        // Copy all original streams by default.
        args.Add("-c");
        args.Add("copy");

        // Count total audio streams in original to know the output audio index offset.
        var originalAudioCount = allStreams.Count;

        // Encode and filter each new audio track.
        for (var i = 0; i < englishStreams.Count; i++)
        {
            var outputAudioIndex = originalAudioCount + i;
            var stream = englishStreams[i];
            var channels = stream.Channels > 0 ? stream.Channels : 2;
            var bitrate = channels <= 2 ? "256k" : "640k";

            args.Add($"-c:a:{outputAudioIndex}");
            args.Add("aac");
            args.Add($"-b:a:{outputAudioIndex}");
            args.Add(bitrate);
            args.Add($"-ac:a:{outputAudioIndex}");
            args.Add(channels.ToString(CultureInfo.InvariantCulture));
            args.Add($"-filter:a:{outputAudioIndex}");
            args.Add($"\"{volumeFilter}\"");

            // Set the track title.
            var originalTitle = string.IsNullOrWhiteSpace(stream.Title)
                ? $"Track {stream.Index}"
                : stream.Title;
            args.Add($"-metadata:s:a:{outputAudioIndex}");
            args.Add($"title=\"{FilteredTrackPrefix}{originalTitle}\"");
            args.Add($"-metadata:s:a:{outputAudioIndex}");
            args.Add("language=eng");

            // Set the filtered track as default, unset default on the original.
            args.Add($"-disposition:a:{outputAudioIndex}");
            args.Add("default");
        }

        // Unset default disposition on original audio tracks.
        for (var i = 0; i < originalAudioCount; i++)
        {
            args.Add($"-disposition:a:{i}");
            args.Add("0");
        }

        args.Add("-y");
        args.Add($"\"{outputPath}\"");

        return string.Join(" ", args);
    }

    private static bool IsEnglishTrack(AudioStreamInfo stream)
    {
        var lang = (stream.Language ?? string.Empty).ToLowerInvariant();
        return lang == "eng" || lang == "en" || lang == "english" || lang == string.Empty;
    }

    private async Task<List<AudioStreamInfo>> ProbeAudioStreamsAsync(
        string mediaPath,
        CancellationToken cancellationToken)
    {
        // Use ffprobe to get audio stream info as JSON.
        var ffprobePath = Path.Combine(
            Path.GetDirectoryName(_ffmpegPath) ?? string.Empty,
            "ffprobe");

        // If ffprobe doesn't exist next to ffmpeg, try the path without directory
        // (in case ffmpeg is just "ffmpeg" on PATH).
        if (!System.IO.File.Exists(ffprobePath))
        {
            ffprobePath = _ffmpegPath.Replace("ffmpeg", "ffprobe");
        }

        var args = $"-v quiet -print_format json -show_streams -select_streams a \"{mediaPath}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ffprobe failed (exit code {process.ExitCode}): {stderr}");
        }

        var probeResult = JsonSerializer.Deserialize<FfprobeResult>(stdout);
        if (probeResult?.Streams == null)
        {
            return new List<AudioStreamInfo>();
        }

        return probeResult.Streams
            .Select(s => new AudioStreamInfo
            {
                Index = s.Index,
                Language = s.Tags?.Language ?? string.Empty,
                Title = s.Tags?.Title ?? string.Empty,
                Channels = s.Channels,
                CodecName = s.CodecName ?? string.Empty,
            })
            .ToList();
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

    private sealed record MuteRegion(double Start, double End);

    private sealed class AudioStreamInfo
    {
        public int Index { get; set; }

        public string Language { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public int Channels { get; set; }

        public string CodecName { get; set; } = string.Empty;
    }

    // --- ffprobe JSON models ---

    private sealed class FfprobeResult
    {
        [JsonPropertyName("streams")]
        public List<FfprobeStream>? Streams { get; set; }
    }

    private sealed class FfprobeStream
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("codec_name")]
        public string? CodecName { get; set; }

        [JsonPropertyName("channels")]
        public int Channels { get; set; }

        [JsonPropertyName("tags")]
        public FfprobeTags? Tags { get; set; }
    }

    private sealed class FfprobeTags
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}
