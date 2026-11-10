using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Comskip.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.ScheduledTasks;

/// <summary>
/// Dashboard → Scheduled Tasks → "Detect commercials (Comskip)". Walks every in-scope item and
/// analyzes anything without a current result. Use it once to backfill an existing library
/// (the replacement for the old batch-comskip.sh).
/// </summary>
public class DetectCommercialsTask : IScheduledTask
{
    private const int PageSize = 200;

    private readonly ILogger<DetectCommercialsTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ItemEligibility _eligibility;
    private readonly CommercialAnalyzer _analyzer;

    /// <summary>Initializes a new instance of the <see cref="DetectCommercialsTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="eligibility">Eligibility rules.</param>
    /// <param name="analyzer">Analyzer.</param>
    public DetectCommercialsTask(
        ILogger<DetectCommercialsTask> logger,
        ILibraryManager libraryManager,
        ItemEligibility eligibility,
        CommercialAnalyzer analyzer)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _eligibility = eligibility;
        _analyzer = analyzer;
    }

    /// <inheritdoc />
    public string Name => "Detect commercials (Comskip)";

    /// <inheritdoc />
    public string Key => "ComskipDetectCommercials";

    /// <inheritdoc />
    public string Description => "Runs comskip on recordings and selected libraries that have not been analyzed yet, and publishes the results as Commercial segments.";

    /// <inheritdoc />
    public string Category => "Comskip";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
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
