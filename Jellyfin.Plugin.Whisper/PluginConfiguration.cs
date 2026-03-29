using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Configuration for the Whisper plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the path to the whisper-cpp binary.
    /// Defaults to "whisper-cpp" (assumes it is on PATH via homebrew).
    /// </summary>
    public string WhisperCppPath { get; set; } = "whisper-cpp";

    /// <summary>
    /// Gets or sets the path to the ffmpeg binary used for audio extraction.
    /// Defaults to "ffmpeg" (assumes it is on PATH).
    /// </summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Gets or sets the whisper model to use.
    /// Passed directly to whisper-cpp's --model flag.
    /// </summary>
    public string WhisperModel { get; set; } = "medium";

    /// <summary>
    /// Gets or sets the list of words or phrases to mute via EDL files.
    /// One entry per line. Case-insensitive. Punctuation is stripped when matching.
    /// </summary>
    public string MuteWords { get; set; } = string.Join(
        "\n",
        "fuck",
        "fucking",
        "fucked",
        "fucker",
        "fuckers",
        "motherfucker",
        "motherfucking",
        "shit",
        "shitting",
        "shitty",
        "bullshit",
        "ass",
        "asshole",
        "assholes",
        "bitch",
        "bitches",
        "bitching",
        "damn",
        "dammit",
        "goddamn",
        "goddammit",
        "bastard",
        "bastards",
        "dick",
        "piss",
        "pissed",
        "crap",
        "crappy");

    /// <summary>
    /// Gets or sets the buffer in milliseconds added before and after each muted region.
    /// Helps ensure the muted word is fully covered. Default: 150ms.
    /// </summary>
    public int EdlBufferMs { get; set; } = 150;
}
