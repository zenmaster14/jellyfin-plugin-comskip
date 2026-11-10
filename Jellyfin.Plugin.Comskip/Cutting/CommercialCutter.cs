using Jellyfin.Plugin.Comskip.Comskip;
using Jellyfin.Plugin.Comskip.Data;
using Jellyfin.Plugin.Comskip.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Cutting;

/// <summary>What happened when a cut was attempted.</summary>
public enum CutOutcome
{
    /// <summary>The recording was rewritten without its commercials.</summary>
    Cut,

    /// <summary>Not possible right now (being watched); try again later.</summary>
    Deferred,

    /// <summary>A safety guard declined the cut; the original is untouched.</summary>
    Skipped,

    /// <summary>The cut failed; the original is untouched.</summary>
    Failed
}

/// <summary>
/// Permanently removes detected commercials from Live TV recordings (.ts), replacing the original
/// file in place. Jellyfin keeps the same library item (its id comes from the path), so watched
/// state and metadata survive, and it re-reads the new runtime on the refresh queued afterwards.
/// </summary>
public sealed class CommercialCutter
{
    private static readonly string[] _tsExtensions = [".ts", ".m2ts", ".mts", ".tp", ".trp"];

    private readonly ILogger<CommercialCutter> _logger;
    private readonly ToolLocator _tools;
    private readonly ItemEligibility _eligibility;
    private readonly CutLedger _ledger;
    private readonly ResultStore _store;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="CommercialCutter"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="tools">Tool locator.</param>
    /// <param name="eligibility">Eligibility rules.</param>
    /// <param name="ledger">Cut ledger.</param>
    /// <param name="store">Result store.</param>
    /// <param name="sessionManager">Session manager (to avoid cutting while watched).</param>
    /// <param name="libraryMonitor">Library monitor.</param>
    /// <param name="providerManager">Provider manager (to refresh the item afterwards).</param>
    /// <param name="fileSystem">File system.</param>
    public CommercialCutter(
        ILogger<CommercialCutter> logger,
        ToolLocator tools,
        ItemEligibility eligibility,
        CutLedger ledger,
        ResultStore store,
        ISessionManager sessionManager,
        ILibraryMonitor libraryMonitor,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _tools = tools;
        _eligibility = eligibility;
        _ledger = ledger;
        _store = store;
        _sessionManager = sessionManager;
        _libraryMonitor = libraryMonitor;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <summary>Returns true when removal is enabled and this analyzed recording should be cut.</summary>
    /// <param name="item">Item.</param>
    /// <param name="result">Its current analysis result.</param>
    /// <returns>True when a cut is due.</returns>
    public bool WantsCut(BaseItem item, AnalysisResult? result)
    {
        return Plugin.Instance?.Configuration.RemoveCommercials == true
            && result is { Status: AnalysisStatus.Success, Cut: false, CutError: null }
            && result.Breaks.Count > 0
            && ResultStore.Matches(result, item.Path)
            && _tsExtensions.Contains(Path.GetExtension(item.Path), StringComparer.OrdinalIgnoreCase)
            && _eligibility.IsLiveTvRecording(item)
            && _ledger.IsHealthy
            && !_ledger.IsCut(item.Id, item.Path);
    }

    /// <summary>Cuts the commercials in <paramref name="result"/> out of the item's file.</summary>
    /// <param name="item">Item.</param>
    /// <param name="result">Current analysis result (must match the file).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<CutOutcome> CutAsync(BaseItem item, AnalysisResult result, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var path = item.Path;

        if (IsBeingWatched(item.Id))
        {
            _logger.LogInformation("Comskip: {Name} is being watched; removing commercials later", item.Name);
            return CutOutcome.Deferred;
        }

        var ffmpeg = _tools.FindFfmpeg();
        var ffprobe = _tools.FindFfprobe();
        if (ffmpeg is null || ffprobe is null)
        {
            return Fail(result, "ffmpeg/ffprobe not found.");
        }

        var cutter = new TsCutter(_logger);
        var duration = await cutter.ProbeDurationAsync(ffprobe, path, cancellationToken).ConfigureAwait(false);
        if (duration is null)
        {
            return Fail(result, "Could not read the recording's length.");
        }

        var keeps = TsCutter.ComputeKeepSegments(result.Breaks, duration.Value);
        var keepSeconds = keeps.Sum(k => k.Length);
        var removeSeconds = duration.Value - keepSeconds;
        if (removeSeconds < 1)
        {
            result.CutError = "Nothing to remove.";
            _store.Save(result);
            return CutOutcome.Skipped;
        }

        var removedPercent = 100 * removeSeconds / duration.Value;
        if (removedPercent > Math.Clamp(config.CutMaxRemovedPercent, 1, 95))
        {
            return Skip(result, $"Detection would remove {removedPercent:0}% of the recording (limit {config.CutMaxRemovedPercent}%).");
        }

        if (keepSeconds < Math.Max(0, config.CutMinRemainingMinutes) * 60)
        {
            return Skip(result, $"Only {keepSeconds / 60:0.0} minutes would remain (minimum {config.CutMinRemainingMinutes}).");
        }

        var dir = Path.GetDirectoryName(path)!;
        var tempOutput = Path.Combine(dir, "." + Path.GetFileNameWithoutExtension(path) + ".comskip-cut.ts");
        var workDir = Path.Combine(Plugin.Instance.WorkPath, "cut-" + item.Id.ToString("N"));

        _logger.LogInformation(
            "Comskip: removing {Count} commercial break(s) ({Seconds:0}s) from {Path}",
            result.Breaks.Count,
            removeSeconds,
            path);

        _libraryMonitor.ReportFileSystemChangeBeginning(path);
        _libraryMonitor.ReportFileSystemChangeBeginning(tempOutput);
        var replaced = false;
        try
        {
            TryDeleteDirectory(workDir);
            var cut = await cutter.CutAsync(ffmpeg, ffprobe, path, keeps, workDir, tempOutput, config.LowPriority, cancellationToken)
                .ConfigureAwait(false);
            if (!cut.Success)
            {
                return Fail(result, cut.Error ?? "Unknown error.");
            }

            // Don't replace a file that changed while we worked (e.g. another post-processor).
            if (!ResultStore.Matches(result, path) || IsBeingWatched(item.Id))
            {
                TryDelete(tempOutput);
                return CutOutcome.Deferred;
            }

            File.Move(tempOutput, path, overwrite: true);
            replaced = true;

            // An .edl sidecar describes the old timeline; it would make Kodi/MPV skip show content now.
            if (config.WriteEdlSidecar)
            {
                TryDelete(Path.ChangeExtension(path, ".edl"));
            }

            var info = new FileInfo(path);
            _ledger.Add(item.Id, new CutEntry
            {
                Path = path,
                FileSize = info.Length,
                LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                CutAtUtc = DateTime.UtcNow,
                OriginalSeconds = Math.Round(duration.Value, 1),
                CutSeconds = Math.Round(cut.ActualSeconds, 1)
            });
            _store.Save(new AnalysisResult
            {
                ItemId = item.Id,
                Path = path,
                FileSize = info.Length,
                LastWriteTicks = info.LastWriteTimeUtc.Ticks,
                AnalyzedAtUtc = DateTime.UtcNow,
                Status = AnalysisStatus.Success,
                DurationSeconds = result.DurationSeconds,
                Cut = true,
                RemovedSeconds = Math.Round(duration.Value - cut.ActualSeconds, 1),
                Breaks = []
            });

            _logger.LogInformation(
                "Comskip: {Name} is now {New:0.0} min (was {Old:0.0} min)",
                item.Name,
                cut.ActualSeconds / 60,
                duration.Value / 60);
            return CutOutcome.Cut;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempOutput);
            return Fail(result, ex.Message);
        }
        finally
        {
            TryDeleteDirectory(workDir);
            if (!replaced)
            {
                TryDelete(tempOutput);
            }

            _libraryMonitor.ReportFileSystemChangeComplete(tempOutput, false);
            _libraryMonitor.ReportFileSystemChangeComplete(path, false);
            if (replaced)
            {
                // Re-read runtime and streams; Jellyfin also prunes keyframes, trickplay and chapter
                // images that belonged to the old timeline.
                _providerManager.QueueRefresh(
                    item.Id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ImageRefreshMode = MetadataRefreshMode.Default
                    },
                    RefreshPriority.High);
            }
        }
    }

    private bool IsBeingWatched(Guid itemId)
    {
        return _sessionManager.Sessions.Any(s => s.NowPlayingItem is not null && s.NowPlayingItem.Id.Equals(itemId));
    }

    private CutOutcome Fail(AnalysisResult result, string error)
    {
        _logger.LogWarning("Comskip: could not remove commercials from {Path}: {Error} The original is unchanged; skip segments are used instead.", result.Path, error);
        result.CutError = error;
        _store.Save(result);
        return CutOutcome.Failed;
    }

    private CutOutcome Skip(AnalysisResult result, string reason)
    {
        _logger.LogWarning("Comskip: not removing commercials from {Path}: {Reason} The original is unchanged; skip segments are used instead.", result.Path, reason);
        result.CutError = reason;
        _store.Save(result);
        return CutOutcome.Skipped;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
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
