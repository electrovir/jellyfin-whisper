using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Configuration for the Whisper plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the path to the whisper-cli binary.
    /// Defaults to "whisper-cli" (assumes it is on PATH via homebrew).
    /// </summary>
    public string WhisperCppPath { get; set; } = "whisper-cli";

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
        "apeshit",
        "ass",
        "asshole",
        "assholes",
        "badass",
        "bastard",
        "bastards",
        "batshit",
        "bitch",
        "bitches",
        "bitching",
        "bullshit",
        "christ",
        "clusterfuck",
        "damn",
        "dammit",
        "dipshit",
        "dumbass",
        "fuck",
        "fucked",
        "fucker",
        "fuckers",
        "fucking",
        "goddamn",
        "goddammit",
        "horseshit",
        "jackass",
        "jesus",
        "mindfuck",
        "motherfucker",
        "motherfucking",
        "oh my god",
        "oh my lord",
        "shit",
        "shitface",
        "shithead",
        "shitting",
        "shitty",
        "smartass");

    /// <summary>
    /// Gets or sets the buffer in milliseconds added before and after each muted region.
    /// Helps ensure the muted word is fully covered. Default: 150ms.
    /// </summary>
    public int EdlBufferMs { get; set; } = 150;
}
