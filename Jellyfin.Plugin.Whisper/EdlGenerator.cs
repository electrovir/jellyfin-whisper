using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Generates EDL (Edit Decision List) mute files from whisper.cpp transcription JSON.
/// Matches word-level tokens against a configurable list of words/phrases and writes
/// a .edl file next to the media file with mute regions (action type 1).
/// </summary>
public class EdlGenerator
{
    private const long MaxDtwTokenMs = 1500;

    private static readonly Regex CensorPattern = new(@"^(-{3,}|\*{2,})", RegexOptions.Compiled);

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EdlGenerator"/> class.
    /// </summary>
    public EdlGenerator(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads a whisper transcription JSON file, finds tokens matching the mute word list,
    /// and writes a .edl file inside the .whisper output directory.
    /// </summary>
    public async Task GenerateAsync(
        string transcriptionJsonPath,
        string mediaPath,
        string muteWordsText,
        int bufferMs,
        CancellationToken cancellationToken)
    {
        var muteEntries = ParseMuteWords(muteWordsText);

        if (muteEntries.Count == 0)
        {
            _logger.LogInformation("No mute words configured, skipping EDL generation for: {MediaPath}", mediaPath);
            return;
        }

        var singleWords = new HashSet<string>(
            muteEntries.Where(w => !w.Contains(' ', StringComparison.Ordinal)),
            StringComparer.OrdinalIgnoreCase);

        var phrases = muteEntries
            .Where(w => w.Contains(' ', StringComparison.Ordinal))
            .Select(w => w.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        var jsonContent = await File.ReadAllTextAsync(transcriptionJsonPath, cancellationToken).ConfigureAwait(false);
        var whisperOutput = JsonSerializer.Deserialize<WhisperJsonOutput>(jsonContent);

        if (whisperOutput?.Transcription == null || whisperOutput.Transcription.Count == 0)
        {
            _logger.LogWarning("No transcription data found in: {Path}", transcriptionJsonPath);
            return;
        }

        ApplyDtwTimings(whisperOutput.Transcription);

        var regions = FindMuteRegions(whisperOutput.Transcription, singleWords, phrases, bufferMs);

        var subtitlesPath = Path.Combine(
            Path.GetDirectoryName(transcriptionJsonPath) ?? string.Empty,
            SubtitleMuteFinder.SubtitlesFileName);
        if (File.Exists(subtitlesPath))
        {
            var srtText = await File.ReadAllTextAsync(subtitlesPath, cancellationToken).ConfigureAwait(false);
            var subtitleRegions = SubtitleMuteFinder.Find(srtText, whisperOutput.Transcription, muteEntries, regions, bufferMs);
            _logger.LogInformation(
                "Subtitles added {Count} mute region(s) whisper missed for: {MediaPath}",
                subtitleRegions.Count,
                mediaPath);
            regions.AddRange(subtitleRegions);
        }

        if (regions.Count == 0)
        {
            _logger.LogInformation("No mute words found in transcription for: {MediaPath}", mediaPath);
            return;
        }

        var merged = MergeRegions(regions);
        var edlPath = GetEdlPath(transcriptionJsonPath);

        await WriteEdlAsync(edlPath, merged, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Generated EDL with {Count} mute region(s) at: {EdlPath}",
            merged.Count,
            edlPath);
    }

    /// <summary>
    /// Computes the .edl file path from the transcription JSON path.
    /// Placed inside the .whisper directory alongside the JSON.
    /// </summary>
    public static string GetEdlPath(string transcriptionJsonPath)
    {
        var dir = Path.GetDirectoryName(transcriptionJsonPath)
            ?? throw new InvalidOperationException($"Could not determine directory for: {transcriptionJsonPath}");
        var name = Path.GetFileNameWithoutExtension(transcriptionJsonPath);
        return Path.Combine(dir, $"{name}.edl");
    }

    private static List<string> ParseMuteWords(string muteWordsText)
    {
        return muteWordsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Replaces each text token's offsets with its DTW-aligned timing when the transcription
    /// was produced with whisper-cli --dtw. Transcriptions without DTW are left unchanged.
    /// </summary>
    private static void ApplyDtwTimings(List<WhisperJsonSegment> segments)
    {
        // whisper.cpp leaves t_dtw at -1 for special tokens and when DTW is disabled.
        var timedTokens = segments
            .SelectMany(s => s.Tokens ?? new List<WhisperJsonToken>())
            .Where(t => t.DtwTime >= 0)
            .ToList();

        for (var i = 0; i < timedTokens.Count; i++)
        {
            // t_dtw is in centiseconds and marks only where the token starts.
            var startMs = (long)(timedTokens[i].DtwTime!.Value * 10);
            var nextStartMs = i + 1 < timedTokens.Count
                ? (long)(timedTokens[i + 1].DtwTime!.Value * 10)
                : startMs + MaxDtwTokenMs;

            timedTokens[i].Offsets = new WhisperJsonOffsets
            {
                From = startMs,
                // The gap before the next token can span a long pause; don't mute all of it.
                To = Math.Clamp(nextStartMs, startMs, startMs + MaxDtwTokenMs),
            };
        }
    }

    private static List<MuteRegion> FindMuteRegions(
        List<WhisperJsonSegment> segments,
        HashSet<string> singleWords,
        List<string[]> phrases,
        int bufferMs)
    {
        var regions = new List<MuteRegion>();

        foreach (var segment in segments)
        {
            if (segment.Tokens == null || segment.Tokens.Count == 0)
            {
                continue;
            }

            // Match single words against each token.
            foreach (var token in segment.Tokens)
            {
                var clean = StripPunctuation(token.Text.Trim());
                if (clean.Length > 0 && singleWords.Contains(clean))
                {
                    regions.Add(new MuteRegion(
                        Math.Max(0, token.Offsets.From - bufferMs),
                        token.Offsets.To + bufferMs,
                        clean));
                }
            }

            // whisper sometimes transcribes profanity censored, as tokens like " f", "---", "ed".
            // Stutters ("I--") use two dashes, so require three or asterisks.
            for (var i = 0; i < segment.Tokens.Count - 1; i++)
            {
                var letter = segment.Tokens[i].Text.Trim();
                if (letter.Length != 1
                    || !singleWords.Any(w => w.StartsWith(letter, StringComparison.OrdinalIgnoreCase))
                    || !CensorPattern.IsMatch(segment.Tokens[i + 1].Text))
                {
                    continue;
                }

                // Include the rest of the word, such as the "ed" in "f---ed".
                var last = i + 1;
                while (last + 1 < segment.Tokens.Count
                    && !segment.Tokens[last + 1].Text.StartsWith(' ')
                    && StripPunctuation(segment.Tokens[last + 1].Text).Length > 0)
                {
                    last++;
                }

                regions.Add(new MuteRegion(
                    Math.Max(0, segment.Tokens[i].Offsets.From - bufferMs),
                    segment.Tokens[last].Offsets.To + bufferMs,
                    string.Concat(segment.Tokens.Skip(i).Take(last - i + 1).Select(t => t.Text)).Trim()));
            }

            // Match multi-word phrases against consecutive tokens.
            foreach (var phrase in phrases)
            {
                if (phrase.Length > segment.Tokens.Count)
                {
                    continue;
                }

                for (var i = 0; i <= segment.Tokens.Count - phrase.Length; i++)
                {
                    var matches = true;
                    for (var j = 0; j < phrase.Length; j++)
                    {
                        var clean = StripPunctuation(segment.Tokens[i + j].Text.Trim());
                        if (!string.Equals(clean, phrase[j], StringComparison.OrdinalIgnoreCase))
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        regions.Add(new MuteRegion(
                            Math.Max(0, segment.Tokens[i].Offsets.From - bufferMs),
                            segment.Tokens[i + phrase.Length - 1].Offsets.To + bufferMs,
                            string.Join(" ", phrase)));
                    }
                }
            }
        }

        return regions;
    }

    private static string StripPunctuation(string text)
    {
        // Keep letters, digits, and apostrophes (for contractions like "shit's").
        return new string(text.Where(c => char.IsLetterOrDigit(c) || c == '\'').ToArray());
    }

    private static List<MuteRegion> MergeRegions(List<MuteRegion> regions)
    {
        var sorted = regions.OrderBy(r => r.StartMs).ToList();
        var merged = new List<MuteRegion> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            if (sorted[i].StartMs <= last.EndMs)
            {
                var combinedWord = last.MatchedWord == sorted[i].MatchedWord
                    ? last.MatchedWord
                    : $"{last.MatchedWord}, {sorted[i].MatchedWord}";
                merged[^1] = new MuteRegion(last.StartMs, Math.Max(last.EndMs, sorted[i].EndMs), combinedWord);
            }
            else
            {
                merged.Add(sorted[i]);
            }
        }

        return merged;
    }

    private static async Task WriteEdlAsync(
        string edlPath,
        List<MuteRegion> regions,
        CancellationToken cancellationToken)
    {
        // EDL format: START_SECONDS<tab>END_SECONDS<tab>ACTION
        // Action 1 = mute audio. Comment lines start with ##.
        var lines = regions.Select(r =>
            string.Format(
                CultureInfo.InvariantCulture,
                "## {2}\n{0:F3}\t{1:F3}\t1",
                r.StartMs / 1000.0,
                r.EndMs / 1000.0,
                r.MatchedWord));

        await File.WriteAllLinesAsync(edlPath, lines, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record MuteRegion(long StartMs, long EndMs, string MatchedWord);
}

// --- whisper.cpp JSON output models (--output-json-full) ---

/// <summary>
/// Root object of whisper.cpp full JSON output.
/// </summary>
public class WhisperJsonOutput
{
    /// <summary>
    /// Gets or sets the transcription segments.
    /// </summary>
    [JsonPropertyName("transcription")]
    public List<WhisperJsonSegment> Transcription { get; set; } = new();
}

/// <summary>
/// A transcription segment containing text and optional word-level tokens.
/// </summary>
public class WhisperJsonSegment
{
    /// <summary>
    /// Gets or sets the full text of the segment.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the segment-level offsets in milliseconds.
    /// </summary>
    [JsonPropertyName("offsets")]
    public WhisperJsonOffsets Offsets { get; set; } = new();

    /// <summary>
    /// Gets or sets the word-level tokens. Present only with --output-json-full.
    /// </summary>
    [JsonPropertyName("tokens")]
    public List<WhisperJsonToken>? Tokens { get; set; }
}

/// <summary>
/// A single word-level token from whisper.cpp full JSON output.
/// </summary>
public class WhisperJsonToken
{
    /// <summary>
    /// Gets or sets the token text (often includes a leading space).
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token offsets in milliseconds.
    /// </summary>
    [JsonPropertyName("offsets")]
    public WhisperJsonOffsets Offsets { get; set; } = new();

    /// <summary>
    /// Gets or sets the token vocabulary ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the token probability (0.0 to 1.0).
    /// </summary>
    [JsonPropertyName("p")]
    public double Probability { get; set; }

    /// <summary>
    /// Gets or sets the DTW-aligned token start time in centiseconds, or -1 when unavailable.
    /// Present only when whisper-cli ran with --dtw (older versions omit it otherwise).
    /// </summary>
    [JsonPropertyName("t_dtw")]
    public double? DtwTime { get; set; }
}

/// <summary>
/// Millisecond offsets for a segment or token.
/// </summary>
public class WhisperJsonOffsets
{
    /// <summary>
    /// Gets or sets the start offset in milliseconds.
    /// </summary>
    [JsonPropertyName("from")]
    public long From { get; set; }

    /// <summary>
    /// Gets or sets the end offset in milliseconds.
    /// </summary>
    [JsonPropertyName("to")]
    public long To { get; set; }
}
