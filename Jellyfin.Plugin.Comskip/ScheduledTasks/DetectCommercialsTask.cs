using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Comskip.Cutting;
using Jellyfin.Plugin.Comskip.Data;
using Jellyfin.Plugin.Comskip.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.ScheduledTasks;

/// <summary>
/// Dashboard → Scheduled Tasks → "Detect commercials (Comskip)". Walks every in-scope item and
/// analyzes anything without a current result, and deletes results for items that no longer
/// exist. Use it once to backfill an existing library (the replacement for the old batch-comskip.sh).
/// </summary>
public class DetectCommercialsTask : IScheduledTask
{
    private const int PageSize = 200;

    private readonly ILogger<DetectCommercialsTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ItemEligibility _eligibility;
    private readonly CommercialAnalyzer _analyzer;
    private readonly ResultStore _store;
    private readonly CutLedger _ledger;
    private readonly AnalysisQueue _queue;

    /// <summary>Initializes a new instance of the <see cref="DetectCommercialsTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="eligibility">Eligibility rules.</param>
    /// <param name="analyzer">Analyzer.</param>
    /// <param name="store">Result store.</param>
    /// <param name="ledger">Cut ledger.</param>
    /// <param name="queue">Analysis queue (for items that must wait).</param>
    public DetectCommercialsTask(
        ILogger<DetectCommercialsTask> logger,
        ILibraryManager libraryManager,
        ItemEligibility eligibility,
        CommercialAnalyzer analyzer,
        ResultStore store,
        CutLedger ledger,
        AnalysisQueue queue)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _eligibility = eligibility;
        _analyzer = analyzer;
        _store = store;
        _ledger = ledger;
        _queue = queue;
    }

    /// <inheritdoc />
    public string Name => "Detect commercials (Comskip)";

    /// <inheritdoc />
    public string Key => "ComskipDetectCommercials";

    /// <inheritdoc />
    public string Description => "Runs comskip on recordings and selected libraries that have not been analyzed yet and publishes the results as Commercial segments. If commercial removal is on, also cuts commercials out of Live TV recordings.";

    /// <inheritdoc />
    public string Category => "Comskip";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        RemoveOrphanedResults();

        _eligibility.Invalidate();
        var (libraryIds, _) = _eligibility.GetScope();
        if (libraryIds.Length == 0)
        {
            _logger.LogInformation("Comskip: no libraries in scope; nothing to do");
            progress.Report(100);
            return;
        }

        var items = new List<BaseItem>();
        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IncludeItemTypes = ItemEligibility.ItemKinds,
            IsVirtualItem = false,
            Recursive = true,
            AncestorIds = libraryIds,
            DtoOptions = new DtoOptions(false),
            Limit = PageSize
        };

        for (var start = 0; ; start += PageSize)
        {
            query.StartIndex = start;
            var page = _libraryManager.GetItemList(query);
            items.AddRange(page.Where(_eligibility.IsEligible));
            if (page.Count < PageSize)
            {
                break;
            }
        }

        _logger.LogInformation("Comskip: {Count} item(s) in scope", items.Count);

        var done = 0;
        var counts = new Dictionary<AnalysisOutcome, int>();
        var parallelism = Math.Clamp(Plugin.Instance?.Configuration.MaxConcurrentJobs ?? 1, 1, 8);

        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                AnalysisOutcome outcome;
                try
                {
                    outcome = await _analyzer.AnalyzeAsync(item, false, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Comskip: failed on {Path}", item.Path);
                    outcome = AnalysisOutcome.Failed;
                }

                if (outcome == AnalysisOutcome.Deferred)
                {
                    // Being recorded or watched right now: let the background queue retry it.
                    _queue.Enqueue(item.Id, TimeSpan.FromMinutes(5));
                }

                lock (counts)
                {
                    counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
                    done++;
                    progress.Report(100.0 * done / items.Count);
                }
            }).ConfigureAwait(false);

        _logger.LogInformation(
            "Comskip: finished. {Summary}",
            string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
        progress.Report(100);
    }

    /// <summary>
    /// Safety net for results whose item is no longer in Jellyfin's library (normally removed the
    /// moment Jellyfin drops the item; this catches removals that happened while Jellyfin was
    /// stopped or the plugin was disabled). Only the library is consulted, never the disk, so an
    /// offline network share can't wipe valid results.
    /// </summary>
    private void RemoveOrphanedResults()
    {
        var removed = 0;
        foreach (var id in _store.ListItemIds())
        {
            if (_libraryManager.GetItemById(id) is null)
            {
                _store.Delete(id);
                removed++;
            }
        }

        foreach (var id in _ledger.ItemIds())
        {
            if (_libraryManager.GetItemById(id) is null)
            {
                _ledger.Remove(id);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Comskip: removed {Count} result file(s) for items no longer in the library", removed);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Nightly sweep catches anything the live watcher missed (e.g. while Jellyfin was down).
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }
}
