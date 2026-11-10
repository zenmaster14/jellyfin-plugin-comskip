using System.Collections.Concurrent;
using System.Diagnostics;
using Jellyfin.Plugin.Comskip.Comskip;
using Jellyfin.Plugin.Comskip.Cutting;
using Jellyfin.Plugin.Comskip.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Services;

/// <summary>What happened when an item was submitted for analysis.</summary>
public enum AnalysisOutcome
{
    /// <summary>Comskip ran and segments were stored.</summary>
    Analyzed,

    /// <summary>A current result already exists.</summary>
    UpToDate,

    /// <summary>The item is outside the configured scope or its file is missing.</summary>
    NotEligible,

    /// <summary>The file is still being recorded or written; try later.</summary>
    Deferred,

    /// <summary>Comskip failed; the failure was recorded.</summary>
    Failed,

    /// <summary>Another run for this item is in progress.</summary>
    AlreadyRunning,

    /// <summary>Commercials were cut out of the recording.</summary>
    Cut,

    /// <summary>Removing commercials was declined by a safety guard or failed; skip segments are used instead.</summary>
    CutNotDone
}

/// <summary>
/// Analyzes one item end to end: checks it is ready, runs comskip, stores the result and asks
/// Jellyfin to refresh the item's media segments so clients see the Skip button.
/// </summary>
public sealed class CommercialAnalyzer : IDisposable
{
    private readonly ILogger<CommercialAnalyzer> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _segmentManager;
    private readonly IServiceProvider _services;
    private readonly ItemEligibility _eligibility;
    private readonly ComskipRunner _runner;
    private readonly ResultStore _store;
    private readonly CommercialCutter _cutter;
    private readonly CutLedger _ledger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, string> _running = new();

    /// <summary>Initializes a new instance of the <see cref="CommercialAnalyzer"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="segmentManager">Media segment manager.</param>
    /// <param name="services">Service provider (for optional Live TV services).</param>
    /// <param name="eligibility">Eligibility rules.</param>
    /// <param name="runner">Comskip runner.</param>
    /// <param name="store">Result store.</param>
    /// <param name="cutter">Commercial cutter.</param>
    /// <param name="ledger">Cut ledger.</param>
    public CommercialAnalyzer(
        ILogger<CommercialAnalyzer> logger,
        ILibraryManager libraryManager,
        IMediaSegmentManager segmentManager,
        IServiceProvider services,
        ItemEligibility eligibility,
        ComskipRunner runner,
        ResultStore store,
        CommercialCutter cutter,
        CutLedger ledger)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _segmentManager = segmentManager;
        _services = services;
        _eligibility = eligibility;
        _runner = runner;
        _store = store;
        _cutter = cutter;
        _ledger = ledger;
        _slots = new SemaphoreSlim(Math.Clamp(Plugin.Instance?.Configuration.MaxConcurrentJobs ?? 1, 1, 8));
    }

    /// <summary>Gets the items currently being analyzed (id → path).</summary>
    public IReadOnlyDictionary<Guid, string> Running => _running;

    /// <summary>Analyzes an item if needed.</summary>
    /// <param name="item">Item.</param>
    /// <param name="force">Re-run even if a current result exists.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<AnalysisOutcome> AnalyzeAsync(BaseItem item, bool force, CancellationToken cancellationToken)
    {
        if (!_eligibility.IsEligible(item) || !File.Exists(item.Path))
        {
            return AnalysisOutcome.NotEligible;
        }

        // A file this plugin already cut: never analyze or cut it again (the cut removed the breaks).
        if (!force && _ledger.IsCut(item.Id, item.Path))
        {
            EnsureCutResult(item);
            return AnalysisOutcome.UpToDate;
        }

        var stored = _store.Get(item.Id);
        var upToDate = !force && ResultStore.Matches(stored, item.Path);
        var cutDue = upToDate && _cutter.WantsCut(item, stored);
        if (upToDate && !cutDue)
        {
            return AnalysisOutcome.UpToDate;
        }

        if (!IsReady(item.Path))
        {
            return AnalysisOutcome.Deferred;
        }

        if (!_running.TryAdd(item.Id, item.Path))
        {
            return AnalysisOutcome.AlreadyRunning;
        }

        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!cutDue)
                {
                    var outcome = await RunAsync(item, cancellationToken).ConfigureAwait(false);
                    if (outcome != AnalysisOutcome.Analyzed)
                    {
                        return outcome;
                    }

                    stored = _store.Get(item.Id);

                    // A forced re-analysis of an already-cut file must never cut it again.
                    if (_ledger.IsCut(item.Id, item.Path) || !_cutter.WantsCut(item, stored))
                    {
                        return AnalysisOutcome.Analyzed;
                    }
                }

                var cut = await _cutter.CutAsync(item, stored!, cancellationToken).ConfigureAwait(false);
                return cut switch
                {
                    CutOutcome.Cut => AnalysisOutcome.Cut,
                    CutOutcome.Deferred => AnalysisOutcome.Deferred,
                    _ => AnalysisOutcome.CutNotDone
                };
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            _running.TryRemove(item.Id, out _);
        }
    }

    /// <summary>Re-pushes stored segments for an item into Jellyfin.</summary>
    /// <param name="item">Item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RefreshSegmentsAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var libraryOptions = _libraryManager.GetLibraryOptions(item);
        await _segmentManager.RunSegmentPluginProviders(item, libraryOptions, false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    private void EnsureCutResult(BaseItem item)
    {
        var existing = _store.Get(item.Id);
        if (ResultStore.Matches(existing, item.Path))
        {
            return;
        }

        // Jellyfin prunes plugin data when it notices the rewritten file; restore an empty result
        // so no stale skip segments are served.
        var info = new FileInfo(item.Path);
        _store.Save(new AnalysisResult
        {
            ItemId = item.Id,
            Path = item.Path,
            FileSize = info.Length,
            LastWriteTicks = info.LastWriteTimeUtc.Ticks,
            AnalyzedAtUtc = DateTime.UtcNow,
            Status = AnalysisStatus.Success,
            Cut = true,
            Breaks = []
        });
    }

    private bool IsReady(string path)
    {
        // Never analyze a recording that is still being written.
        var recordings = _services.GetService<IRecordingsManager>();
        if (recordings?.GetActiveRecordingInfo(path) is not null)
        {
            return false;
        }

        // Wait for other writers (post-processors, copies) to settle.
        var settle = TimeSpan.FromSeconds(Math.Max(0, Plugin.Instance?.Configuration.SettleSeconds ?? 120));
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= settle;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<AnalysisOutcome> RunAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var info = new FileInfo(item.Path);
        var size = info.Length;
        var lastWrite = info.LastWriteTimeUtc.Ticks;
        var workDir = Path.Combine(Plugin.Instance.WorkPath, item.Id.ToString("N"));
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Comskip: analyzing {Name} ({Path})", item.Name, item.Path);

        ComskipRun run;
        try
        {
            TryDeleteDirectory(workDir);
            run = await _runner.RunAsync(item.Path, workDir, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Comskip: unexpected error analyzing {Path}", item.Path);
            run = new ComskipRun(false, [], ex.Message);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }

        // The file may have been replaced (e.g. remuxed by a post-processor) while we worked.
        if (!File.Exists(item.Path) || new FileInfo(item.Path).Length != size)
        {
            _logger.LogInformation("Comskip: {Path} changed during analysis; discarding the result", item.Path);
            return AnalysisOutcome.Deferred;
        }

        double? runtime = item.RunTimeTicks is > 0 ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : null;
        var breaks = run.Success
            ? EdlParser.Shape(run.Breaks, config.StartPaddingSeconds, config.EndPaddingSeconds, config.MinSegmentSeconds, runtime)
            : new List<CommercialBreak>();

        _store.Save(new AnalysisResult
        {
            ItemId = item.Id,
            Path = item.Path,
            FileSize = size,
            LastWriteTicks = lastWrite,
            AnalyzedAtUtc = DateTime.UtcNow,
            Status = run.Success ? AnalysisStatus.Success : AnalysisStatus.Failed,
            Error = run.Error,
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            Breaks = breaks
        });

        if (!run.Success)
        {
            _logger.LogWarning("Comskip: analysis failed for {Path}: {Error}", item.Path, run.Error);
            return AnalysisOutcome.Failed;
        }

        _logger.LogInformation(
            "Comskip: found {Count} commercial break(s) in {Name} in {Seconds:0}s",
            breaks.Count,
            item.Name,
            stopwatch.Elapsed.TotalSeconds);

        if (config.WriteEdlSidecar)
        {
            WriteSidecar(item.Path, breaks);
        }

        try
        {
            await RefreshSegmentsAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Comskip: stored result but could not refresh media segments for {Path}", item.Path);
        }

        return AnalysisOutcome.Analyzed;
    }

    private void WriteSidecar(string mediaPath, List<CommercialBreak> breaks)
    {
        var edlPath = Path.ChangeExtension(mediaPath, ".edl");
        try
        {
            // The sidecar would bump the folder's mtime, not the media file's, so it does not
            // invalidate our own result.
            File.WriteAllText(edlPath, EdlParser.ToEdl(breaks));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Comskip: could not write {EdlPath}", edlPath);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
