using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Finds mute words that appear in a subtitle file but that whisper did not transcribe.
/// Subtitles say what was spoken but only roughly when (a cue spans several seconds), so each
/// missed word is placed between the whisper words that surround it in the subtitle line.
/// </summary>
internal static class SubtitleMuteFinder
{
    /// <summary>
    /// File name of the extracted subtitles inside the .whisper directory.
    /// </summary>
    public const string SubtitlesFileName = "subtitles.srt";

    /// <summary>
    /// How far outside a cue's time range to look for its words in the transcription.
    /// </summary>
    private const long CueSearchPaddingMs = 2000;

    /// <summary>
    /// Gaps between surrounding words shorter than this mean the missed word was absorbed into one of them.
    /// </summary>
    private const long MinGapMs = 200;

    private static readonly Regex CueTimingPattern = new(
        @"(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)",
        RegexOptions.Compiled);

    private static readonly Regex MarkupPattern = new(@"<[^>]*>|\{[^}]*\}", RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled);

    /// <summary>
    /// Returns mute regions for subtitle mute words that are not already covered by <paramref name="existingRegions"/>.
    /// </summary>
    public static List<EdlGenerator.MuteRegion> Find(
        string srtText,
        List<WhisperJsonSegment> segments,
        IReadOnlyCollection<string> muteEntries,
        IReadOnlyList<EdlGenerator.MuteRegion> existingRegions,
        int bufferMs)
    {
        var entries = muteEntries.Select(SplitWords).Where(words => words.Count > 0).ToList();
        var whisperWords = GetWhisperWords(segments);
        var regions = new List<EdlGenerator.MuteRegion>();

        foreach (var cue in ParseCues(srtText))
        {
            var cueWords = SplitWords(cue.Text);
            var occurrences = FindOccurrences(cueWords, entries);
            if (occurrences.Count == 0)
            {
                continue;
            }

            var window = whisperWords
                .Where(w => w.FromMs >= cue.StartMs - CueSearchPaddingMs && w.FromMs <= cue.EndMs + CueSearchPaddingMs)
                .ToList();
            var matches = AlignWords(cueWords, window);

            foreach (var (first, last, phrase) in occurrences)
            {
                var matchedCount = matches.Count(m => m.CueIndex >= first && m.CueIndex <= last);
                if (matchedCount == last - first + 1)
                {
                    // whisper transcribed this word, so the transcription matching already handled it.
                    continue;
                }

                var beforeIndex = matches.FindLastIndex(m => m.CueIndex < first);
                var afterIndex = matches.FindIndex(m => m.CueIndex > last);
                var before = beforeIndex >= 0 ? matches[beforeIndex].Word : null;
                var after = afterIndex >= 0 ? matches[afterIndex].Word : null;

                var startMs = before?.ToMs ?? cue.StartMs;
                var endMs = after?.FromMs ?? cue.EndMs;
                if (endMs - startMs < MinGapMs)
                {
                    startMs = before?.FromMs ?? cue.StartMs;
                    endMs = after?.ToMs ?? cue.EndMs;
                }

                if (endMs <= startMs)
                {
                    continue;
                }

                var isCovered = existingRegions.Concat(regions)
                    .Any(r => r.StartMs < endMs && r.EndMs > startMs);
                if (isCovered)
                {
                    continue;
                }

                regions.Add(new EdlGenerator.MuteRegion(
                    Math.Max(0, startMs - bufferMs),
                    endMs + bufferMs,
                    $"{phrase} (subtitles)"));
            }
        }

        return regions;
    }

    private static List<(int First, int Last, string Phrase)> FindOccurrences(
        List<string> cueWords,
        List<List<string>> entries)
    {
        var occurrences = new List<(int First, int Last, string Phrase)>();

        foreach (var entry in entries)
        {
            for (var i = 0; i <= cueWords.Count - entry.Count; i++)
            {
                if (entry.Select((word, j) => cueWords[i + j] == word).All(isMatch => isMatch))
                {
                    occurrences.Add((i, i + entry.Count - 1, string.Join(' ', entry)));
                }
            }
        }

        return occurrences;
    }

    /// <summary>
    /// Longest-common-subsequence alignment of a subtitle line's words against nearby whisper words.
    /// </summary>
    private static List<(int CueIndex, TimedWord Word)> AlignWords(List<string> cueWords, List<TimedWord> window)
    {
        var lengths = new int[cueWords.Count + 1, window.Count + 1];
        for (var i = cueWords.Count - 1; i >= 0; i--)
        {
            for (var j = window.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = cueWords[i] == window[j].Text
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var matches = new List<(int CueIndex, TimedWord Word)>();
        var cueIndex = 0;
        var windowIndex = 0;
        while (cueIndex < cueWords.Count && windowIndex < window.Count)
        {
            if (cueWords[cueIndex] == window[windowIndex].Text)
            {
                matches.Add((cueIndex, window[windowIndex]));
                cueIndex++;
                windowIndex++;
            }
            else if (lengths[cueIndex + 1, windowIndex] >= lengths[cueIndex, windowIndex + 1])
            {
                cueIndex++;
            }
            else
            {
                windowIndex++;
            }
        }

        return matches;
    }

    /// <summary>
    /// Joins whisper's sub-word tokens into words. A token with a leading space starts a new word.
    /// </summary>
    private static List<TimedWord> GetWhisperWords(List<WhisperJsonSegment> segments)
    {
        var words = new List<TimedWord>();

        foreach (var segment in segments)
        {
            var text = string.Empty;
            long fromMs = 0;
            long toMs = 0;

            foreach (var token in segment.Tokens ?? new List<WhisperJsonToken>())
            {
                // Special tokens such as [_BEG_] and [_TT_123] carry no speech.
                if (token.Text.StartsWith("[_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (token.Text.StartsWith(' ') && text.Length > 0)
                {
                    words.AddRange(SplitWords(text).Select(w => new TimedWord(w, fromMs, toMs)));
                    text = string.Empty;
                }

                if (text.Length == 0)
                {
                    fromMs = token.Offsets.From;
                }

                text += token.Text;
                toMs = token.Offsets.To;
            }

            words.AddRange(SplitWords(text).Select(w => new TimedWord(w, fromMs, toMs)));
        }

        return words;
    }

    private static List<Cue> ParseCues(string srtText)
    {
        return Regex.Split(srtText.Replace("\r\n", "\n", StringComparison.Ordinal), @"\n\s*\n")
            .Select(block =>
            {
                var timing = CueTimingPattern.Match(block);
                if (!timing.Success)
                {
                    return null;
                }

                var text = block.Substring(timing.Index + timing.Length);
                return new Cue(ToMs(timing, 1), ToMs(timing, 5), MarkupPattern.Replace(text, " "));
            })
            .OfType<Cue>()
            .ToList();
    }

    private static long ToMs(Match match, int firstGroup)
    {
        long Group(int offset) => long.Parse(match.Groups[firstGroup + offset].Value, CultureInfo.InvariantCulture);
        return (((Group(0) * 60) + Group(1)) * 60 * 1000) + (Group(2) * 1000) + Group(3);
    }

    private static List<string> SplitWords(string text)
    {
        return WordPattern.Matches(text.Replace('’', '\'').ToLowerInvariant())
            .Select(m => m.Value.Trim('\''))
            .Where(w => w.Length > 0)
            .ToList();
    }

    private sealed record Cue(long StartMs, long EndMs, string Text);

    private sealed record TimedWord(string Text, long FromMs, long ToMs);
}
