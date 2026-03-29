# Jellyfin Whisper Transcription Plugin

A Jellyfin plugin that automatically transcribes media files using [whisper.cpp](https://github.com/ggml-org/whisper.cpp), generates EDL mute files for profanity filtering, and bakes filtered audio tracks directly into MKV files so muting works with direct play on all clients.

## What It Does

1. Extracts audio from every movie and episode in your Jellyfin library.
2. Runs whisper.cpp to produce word-level transcription JSON.
3. Scans the transcription for configurable mute words/phrases and generates a `.edl` file with mute timestamps.
4. Adds filtered audio tracks to the MKV with those words muted. Original audio is preserved.

Users can switch between the filtered and original audio tracks in the Jellyfin player on any client (web, iOS, Apple TV, etc.) without any transcoding.

Output is stored in a `.whisper/` directory next to each media file:

```
/media/movies/
├── Movie.mkv              # Now contains filtered + original audio tracks
└── Movie.whisper/
    ├── transcription.json  # Full whisper output with per-word timestamps
    ├── transcription.edl   # Mute regions for matched words (with comments)
    ├── .edl-applied        # Marker indicating filtered tracks were added
    └── .complete           # Marker indicating processing finished
```

## Prerequisites

You must install these yourself -- the plugin does **not** install them automatically.

### whisper.cpp

The plugin calls the `whisper-cli` binary (from the whisper.cpp project). Install via Homebrew:

```sh
brew install whisper-cpp
```

The binary is named `whisper-cli` (not `whisper-cpp`). Verify it works:

```sh
whisper-cli --help
```

Or build from source: https://github.com/ggml-org/whisper.cpp#building

You also need a whisper model file. The `--model` flag requires a **full file path** to a downloaded `.bin` model, not just a name like "medium". Download a model:

```sh
# Download the medium model (~1.5 GB)
curl -L -o ~/ggml-medium.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin
```

Then set the "Whisper Model" field in the plugin config to the full path (e.g. `/Users/media/ggml-medium.bin`).

Available models (trade-off between speed and accuracy): `ggml-tiny.bin`, `ggml-base.bin`, `ggml-small.bin`, `ggml-medium.bin`, `ggml-large.bin`. Browse all models at https://huggingface.co/ggerganov/whisper.cpp/tree/main

### ffmpeg

Used to extract audio from media files and to create filtered audio tracks. Most Jellyfin installations already have ffmpeg available. If not:

```sh
brew install ffmpeg
```

## Installation

1. Download `Jellyfin.Plugin.Whisper.dll` from the [latest release](../../releases/latest).
2. Copy it into a `Whisper/` folder inside your Jellyfin plugins directory:
   - **Linux**: `~/.local/share/jellyfin/plugins/Whisper/` (or `/var/lib/jellyfin/plugins/Whisper/` for package installs)
   - **macOS**: `~/Library/Application Support/jellyfin/plugins/Whisper/`
   - **Windows**: `%LocalAppData%\jellyfin\plugins\Whisper\` (or `%ProgramData%\Jellyfin\Server\plugins\Whisper\` for tray installs)

   See the [Jellyfin plugin docs](https://jellyfin.org/docs/general/server/plugins/) and [configuration docs](https://jellyfin.org/docs/general/administration/configuration/) for details on data directory paths, which can be overridden with `--datadir` or the `JELLYFIN_DATA_DIR` environment variable.
3. Restart Jellyfin.

## Configuration

Open **Dashboard > Plugins > Whisper Transcription** in the Jellyfin web UI.

### Settings

| Setting | Default | Description |
|---|---|---|
| **whisper-cpp Binary Path** | `/opt/homebrew/bin/whisper-cli` | Full path to the whisper-cli binary. See [Finding binary paths](#finding-binary-paths) below. |
| **ffmpeg Binary Path** | `ffmpeg` | Path to ffmpeg. Usually already available in Jellyfin. |
| **Whisper Model** | *(empty - must be set)* | Full file path to a downloaded `.bin` model file (e.g. `/Users/media/ggml-medium.bin`). See [Prerequisites](#whisper-cpp). |
| **Mute Words / Phrases** | Common profanity list | One word or phrase per line. Case-insensitive. Punctuation is stripped when matching. Multi-word phrases match against consecutive whisper tokens. |
| **EDL Buffer (ms)** | `150` | Milliseconds of buffer added before and after each muted word to ensure full coverage. |

### Finding binary paths

Jellyfin runs as a system process, not through your shell, so it does **not** have access to your shell's `PATH` (e.g. from `.zshrc`). You must provide full absolute paths to binaries.

To find where Homebrew installed whisper-cli:

```sh
which whisper-cli
```

This will output something like `/opt/homebrew/bin/whisper-cli` (Apple Silicon) or `/usr/local/bin/whisper-cli` (Intel Mac). Paste that full path into the plugin config.

If `which` finds nothing, try:

```sh
ls $(brew --prefix whisper-cpp)/bin/
```

### Actions

- **Regenerate All EDL Files** -- Re-creates EDL files from existing transcription JSON using the current word list. Also clears `.edl-applied` markers so filtered audio tracks will be re-created with the updated word list on the next processing run.
- **Reset All Transcriptions** -- Wipes `.complete` markers so all media will be re-transcribed on the next run.

## How Processing Works

Processing is triggered three ways:

1. **On startup** -- the plugin scans the library and queues any media that needs processing or re-filtering.
2. **Scheduled task** -- "Process Media with Whisper" runs daily at 2 AM UTC by default (configurable in Jellyfin's scheduled tasks).
3. **Library events** -- newly added media is queued automatically.

Files are processed one at a time in a background queue. For each media file:

1. ffmpeg extracts audio as 16 kHz mono WAV (required by whisper.cpp).
2. whisper.cpp transcribes the audio with `--output-json-full` for word-level timestamps.
3. The EDL generator scans tokens against the mute word list, adds the configured buffer around matches, merges overlapping regions, and writes the `.edl` file.
4. For each English audio track in the MKV, a filtered copy is created with the muted words silenced (volume=0) and added as a new track named "(filtered) Original Track Name".
5. The filtered tracks are set as default. Users can switch to the original unfiltered tracks in their player at any time.
6. The intermediate WAV is cleaned up.

### Filtered Audio Tracks

The plugin adds new audio tracks to your MKV files with profanity muted. The original audio tracks are **never modified or removed**.

- Filtered tracks are named `(filtered) <original track name>` (e.g., "(filtered) Stereo")
- Filtered tracks are set as the default audio track
- Original tracks remain selectable in the player
- Encoded as AAC (256 kbps for stereo, 640 kbps for surround)
- Works with direct play on all clients -- no transcoding needed
- Fully reversible: regenerate EDL files to re-create filtered tracks with a different word list

### Processing Time

Transcription is the bottleneck. Rough estimates per hour of media:

| Model | CPU Time | Accuracy |
|---|---|---|
| `tiny` | ~1-2 min | Low |
| `base` | ~2-4 min | Fair |
| `small` | ~5-10 min | Good |
| `medium` | ~15-30 min | Very good |
| `large` | ~30-60 min | Best |

Times vary by hardware. whisper.cpp supports Metal (Apple Silicon) and CUDA (NVIDIA) GPU acceleration out of the box, which can be significantly faster than the CPU estimates above.

## EDL File Format

The generated `.edl` files use tab-separated values with comments indicating the matched word:

```
## damn
12.350	13.100	1
## oh my god
45.800	46.550	1
```

Action type `1` means mute. Comment lines starting with `##` show which word/phrase triggered each mute region.

## API Endpoints

All endpoints require admin authentication.

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/Whisper/RegenerateEdls` | Regenerate EDL files from existing transcriptions and clear `.edl-applied` markers for re-filtering. |
| `POST` | `/Whisper/WipeAllMarkers` | Wipe all `.complete` markers and re-queue everything. |
| `POST` | `/Whisper/ProcessItem/{itemId}` | Delete existing output and re-process a single item. |

## Logging

The plugin writes a dedicated log file at `<plugin-dir>/whisper.log` (e.g. `~/Library/Application Support/jellyfin/plugins/Whisper/whisper.log`). This contains only whisper plugin activity and is much smaller than the full Jellyfin log.

Watch it live:

```sh
tail -f ~/Library/Application\ Support/jellyfin/plugins/Whisper/whisper.log
```
