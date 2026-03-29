using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Long-running background service that processes media files through whisper.cpp
/// one at a time. Acts as the single point of entry for all whisper work:
/// startup scan, library-change events, scheduled task, and API requests.
/// </summary>
public class WhisperQueueService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<WhisperQueueService> _logger;

    private readonly Channel<QueueEntry> _channel = Channel.CreateUnbounded<QueueEntry>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Tracks paths already sitting in the queue so we don't enqueue duplicates.
    /// Entries are removed after processing completes (success or failure).
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    private const int MaxConsecutiveFailures = 5;

    private CancellationTokenSource? _cts;
    private Task? _consumerTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperQueueService"/> class.
    /// </summary>
    public WhisperQueueService(ILibraryManager libraryManager, ILogger<WhisperQueueService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a media file for whisper processing.
    /// Returns false if the file is already queued or already processed (unless force is true).
    /// </summary>
    public bool TryEnqueue(string mediaPath, bool force = false)
    {
        if (!force)
        {
            var whisperDir = WhisperProcessor.GetWhisperDirectory(mediaPath);
            var alreadyProcessed = WhisperProcessor.IsAlreadyProcessed(whisperDir);
            var needsFiltering = WhisperProcessor.NeedsEdlFiltering(whisperDir);

            // Skip if fully processed AND doesn't need re-filtering.
            if (alreadyProcessed && !needsFiltering)
            {
                return false;
            }
        }

        // Deduplicate: only enqueue if not already pending.
        if (!_pending.TryAdd(mediaPath, 0))
        {
            return false;
        }

        _channel.Writer.TryWrite(new QueueEntry(mediaPath, force));
        return true;
    }

    /// <summary>
    /// Enqueues a media file for reprocessing. Deletes existing output first.
    /// </summary>
    public bool EnqueueForReprocessing(string mediaPath)
    {
        var whisperDir = WhisperProcessor.GetWhisperDirectory(mediaPath);
        if (Directory.Exists(whisperDir))
        {
            try
            {
                Directory.Delete(whisperDir, recursive: true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete existing whisper dir: {Path}", whisperDir);
            }
        }

        return TryEnqueue(mediaPath, force: true);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _consumerTask = ConsumeAsync(_cts.Token);

        // Scan for unprocessed items on a background thread so StartAsync returns quickly.
        _ = Task.Run(() => EnqueueUnprocessedItems(), CancellationToken.None);

        _logger.LogInformation("Whisper queue service started.");
        WhisperFileLogger.Info($"Whisper queue service started. Log file: {WhisperFileLogger.LogPath}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;

        _cts?.Cancel();
        _channel.Writer.TryComplete();

        if (_consumerTask != null)
        {
            // Wait up to 10 seconds for the consumer to finish; don't block shutdown.
            await Task.WhenAny(_consumerTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false);
        }

        _logger.LogInformation("Whisper queue service stopped.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
    }

    /// <summary>
    /// Scans the entire library and enqueues any items that are missing
    /// a .whisper folder or .complete marker.
    /// </summary>
    public void EnqueueUnprocessedItems()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        });

        var queued = 0;

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                continue;
            }

            if (TryEnqueue(item.Path))
            {
                queued++;
            }
        }

        _logger.LogInformation("Library scan complete: queued {Count} unprocessed item(s).", queued);
        WhisperFileLogger.Info($"Library scan complete: queued {queued} unprocessed item(s).");
    }

    private void RefreshLibraryItem(string mediaPath)
    {
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Path = mediaPath,
                Recursive = false,
            });

            foreach (var item in items)
            {
                _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, CancellationToken.None);
                WhisperFileLogger.Info($"Notified Jellyfin of file change: {mediaPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh library item: {Path}", mediaPath);
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Video || string.IsNullOrEmpty(e.Item.Path))
        {
            return;
        }

        if (TryEnqueue(e.Item.Path))
        {
            _logger.LogInformation("New media detected, queued for whisper: {Path}", e.Item.Path);
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var plugin = WhisperPlugin.Instance;
                    if (plugin == null)
                    {
                        _logger.LogError("WhisperPlugin.Instance is null, cannot process media.");
                        break;
                    }

                    var config = plugin.Configuration;
                    var processor = new WhisperProcessor(config, _logger);
                    var whisperDir = WhisperProcessor.GetWhisperDirectory(entry.MediaPath);

                    // Check if this item only needs EDL re-filtering (transcription already done).
                    if (WhisperProcessor.IsAlreadyProcessed(whisperDir) && WhisperProcessor.NeedsEdlFiltering(whisperDir))
                    {
                        WhisperFileLogger.Info($"Re-filtering: {entry.MediaPath}");
                        await processor.ApplyEdlFilteringAsync(entry.MediaPath, whisperDir, cancellationToken).ConfigureAwait(false);
                        consecutiveFailures = 0;
                        WhisperFileLogger.Info($"Re-filtering complete: {entry.MediaPath}");
                        RefreshLibraryItem(entry.MediaPath);
                    }
                    else
                    {
                        var logMsg = $"Processing: {entry.MediaPath} (whisper-cli: {config.WhisperCppPath}, model: {config.WhisperModel})";
                        _logger.LogInformation("{Message}", logMsg);
                        WhisperFileLogger.Info(logMsg);

                        await processor.ProcessAsync(entry.MediaPath, whisperDir, cancellationToken).ConfigureAwait(false);
                        consecutiveFailures = 0;
                        WhisperFileLogger.Info($"Complete: {entry.MediaPath}");
                        RefreshLibraryItem(entry.MediaPath);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process: {Path}", entry.MediaPath);
                    WhisperFileLogger.Error($"Failed to process: {entry.MediaPath} - {ex.Message}");
                    consecutiveFailures++;

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _logger.LogError(
                            "Whisper queue halted after {Count} consecutive failures. Fix the issue (check binary paths in plugin config) and restart Jellyfin or run the scheduled task to retry.",
                            consecutiveFailures);
                        break;
                    }
                }
                finally
                {
                    _pending.TryRemove(entry.MediaPath, out _);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    private sealed record QueueEntry(string MediaPath, bool Force);
}
