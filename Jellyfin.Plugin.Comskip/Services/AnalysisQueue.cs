using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Services;

/// <summary>
/// Background queue fed by library events, the segment provider and the API. Items that are not
/// ready yet (still recording) are retried every few minutes for up to a day.
/// </summary>
public sealed class AnalysisQueue : BackgroundService
{
    private static readonly TimeSpan _retryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _giveUpAfter = TimeSpan.FromHours(24);

    private readonly ILogger<AnalysisQueue> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly CommercialAnalyzer _analyzer;
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, Entry> _pending = new();
    private readonly ConcurrentQueue<string> _recent = new();
    private CancellationToken _stopping = CancellationToken.None;

    /// <summary>Initializes a new instance of the <see cref="AnalysisQueue"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="analyzer">Analyzer.</param>
    public AnalysisQueue(ILogger<AnalysisQueue> logger, ILibraryManager libraryManager, CommercialAnalyzer analyzer)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _analyzer = analyzer;
    }

    /// <summary>Gets the number of items waiting (including delayed retries).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Gets recent activity lines, newest last.</summary>
    public IReadOnlyCollection<string> Recent => _recent.ToArray();

    /// <summary>Queues an item for analysis. Duplicate requests are merged.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="delay">Delay before the item becomes runnable.</param>
    /// <param name="force">Re-run even if a current result exists.</param>
    public void Enqueue(Guid itemId, TimeSpan delay, bool force = false)
    {
        var added = false;
        _pending.AddOrUpdate(
            itemId,
            _ =>
            {
                added = true;
                return new Entry(DateTime.UtcNow, force);
            },
            (_, existing) => existing with { Force = existing.Force || force });

        if (!added)
        {
            return;
        }

        if (delay <= TimeSpan.Zero)
        {
            _channel.Writer.TryWrite(itemId);
        }
        else
        {
            _ = WriteLaterAsync(itemId, delay);
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var workers = Math.Clamp(Plugin.Instance?.Configuration.MaxConcurrentJobs ?? 1, 1, 8);
        return Task.WhenAll(Enumerable.Range(0, workers).Select(_ => WorkerAsync(stoppingToken)));
    }

    private async Task WorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (var itemId in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_pending.TryGetValue(itemId, out var entry))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                _pending.TryRemove(itemId, out _);
                continue;
            }

            AnalysisOutcome outcome;
            try
            {
                outcome = await _analyzer.AnalyzeAsync(item, entry.Force, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Comskip: queue failed for {ItemId}", itemId);
                outcome = AnalysisOutcome.Failed;
            }

            if (outcome is AnalysisOutcome.Deferred or AnalysisOutcome.AlreadyRunning
                && DateTime.UtcNow - entry.FirstQueuedUtc < _giveUpAfter)
            {
                _ = WriteLaterAsync(itemId, _retryInterval);
                continue;
            }

            _pending.TryRemove(itemId, out _);
            if (outcome is AnalysisOutcome.Analyzed or AnalysisOutcome.Failed)
            {
                Remember($"{DateTime.Now:yyyy-MM-dd HH:mm} {outcome}: {item.Name}");
            }
        }
    }

    private async Task WriteLaterAsync(Guid itemId, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stopping).ConfigureAwait(false);
            _channel.Writer.TryWrite(itemId);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void Remember(string line)
    {
        _recent.Enqueue(line);
        while (_recent.Count > 25)
        {
            _recent.TryDequeue(out _);
        }
    }

    private sealed record Entry(DateTime FirstQueuedUtc, bool Force);
}
