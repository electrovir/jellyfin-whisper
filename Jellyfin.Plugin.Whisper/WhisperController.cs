using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// API controller exposing whisper operations for the plugin config page
/// and external callers (e.g. custom JavaScript, curl).
/// </summary>
[ApiController]
[Route("Whisper")]
[Authorize(Policy = "RequiresElevation")]
public class WhisperController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly WhisperQueueService _queueService;
    private readonly ILogger<WhisperController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperController"/> class.
    /// </summary>
    public WhisperController(
        ILibraryManager libraryManager,
        WhisperQueueService queueService,
        ILogger<WhisperController> logger)
    {
        _libraryManager = libraryManager;
        _queueService = queueService;
        _logger = logger;
    }

    /// <summary>
    /// Wipes all .complete marker files and re-enqueues every media item
    /// for processing.
    /// </summary>
    [HttpPost("WipeAllMarkers")]
    public IActionResult WipeAllMarkers()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        });

        var wiped = 0;

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            var whisperDir = WhisperProcessor.GetWhisperDirectory(item.Path);
            var markerPath = Path.Combine(whisperDir, ".complete");

            if (!System.IO.File.Exists(markerPath))
            {
                continue;
            }

            try
            {
                System.IO.File.Delete(markerPath);
                wiped++;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete marker: {Path}", markerPath);
            }
        }

        _logger.LogInformation("Wiped {Count} .complete markers. Re-scanning library.", wiped);

        // Re-scan so wiped items get queued immediately.
        _queueService.EnqueueUnprocessedItems();

        return Ok(new { wiped });
    }

    /// <summary>
    /// Regenerates EDL mute files for all media items that already have
    /// whisper transcription JSON. Does not re-run whisper-cpp.
    /// Useful after changing the mute word list or buffer setting.
    /// </summary>
    [HttpPost("RegenerateEdls")]
    public async Task<IActionResult> RegenerateEdls()
    {
        var config = WhisperPlugin.Instance?.Configuration ?? new PluginConfiguration();

        if (string.IsNullOrWhiteSpace(config.MuteWords))
        {
            return Ok(new { generated = 0, message = "No mute words configured." });
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        });

        var generated = 0;
        var edlGenerator = new EdlGenerator(_logger);

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            var whisperDir = WhisperProcessor.GetWhisperDirectory(item.Path);
            var transcriptionPath = Path.Combine(whisperDir, "transcription.json");

            if (!System.IO.File.Exists(transcriptionPath))
            {
                continue;
            }

            try
            {
                await edlGenerator.GenerateAsync(
                    transcriptionPath,
                    item.Path,
                    config.MuteWords,
                    config.EdlBufferMs,
                    CancellationToken.None).ConfigureAwait(false);
                generated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate EDL for: {Path}", item.Path);
            }
        }

        _logger.LogInformation("Regenerated {Count} EDL file(s).", generated);
        return Ok(new { generated });
    }

    /// <summary>
    /// Deletes existing whisper output for a single media item and enqueues it
    /// for reprocessing. Returns immediately.
    /// </summary>
    [HttpPost("ProcessItem/{itemId}")]
    public IActionResult ProcessItem([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);

        if (item == null)
        {
            return NotFound(new { error = "Item not found." });
        }

        if (string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
        {
            return BadRequest(new { error = "Item has no accessible file path." });
        }

        _queueService.EnqueueForReprocessing(item.Path);

        return Ok(new { message = $"Queued for reprocessing: {item.Name}" });
    }
}
