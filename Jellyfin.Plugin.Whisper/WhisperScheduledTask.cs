using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Whisper;

/// <summary>
/// Scheduled task that triggers a library-wide scan for unprocessed media.
/// Actual processing happens in <see cref="WhisperQueueService"/> one file at a time.
/// </summary>
public class WhisperScheduledTask : IScheduledTask
{
    private readonly WhisperQueueService _queueService;
    private readonly ILogger<WhisperScheduledTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperScheduledTask"/> class.
    /// </summary>
    public WhisperScheduledTask(WhisperQueueService queueService, ILogger<WhisperScheduledTask> logger)
    {
        _queueService = queueService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Process Media with Whisper";

    /// <inheritdoc />
    public string Key => "WhisperProcessMedia";

    /// <inheritdoc />
    public string Description => "Scans media files and queues any without transcriptions for whisper.cpp processing.";

    /// <inheritdoc />
    public string Category => "Whisper";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Whisper scheduled task: scanning for unprocessed media.");
        _queueService.EnqueueUnprocessedItems();
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
            },
        };
    }
}
