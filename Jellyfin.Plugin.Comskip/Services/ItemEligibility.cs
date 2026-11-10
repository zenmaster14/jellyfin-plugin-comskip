using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Services;

/// <summary>Decides which library items the plugin analyzes.</summary>
public class ItemEligibility
{
    private static readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _config;
    private readonly ILogger<ItemEligibility> _logger;
    private readonly Lock _lock = new();

    private DateTime _cachedAt = DateTime.MinValue;
    private Scope _cached = new(Array.Empty<Guid>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Initializes a new instance of the <see cref="ItemEligibility"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="config">Server configuration.</param>
    /// <param name="logger">Logger.</param>
    public ItemEligibility(ILibraryManager libraryManager, IServerConfigurationManager config, ILogger<ItemEligibility> logger)
    {
        _libraryManager = libraryManager;
        _config = config;
        _logger = logger;
    }

    /// <summary>Gets the item kinds that can be analyzed.</summary>
    public static BaseItemKind[] ItemKinds { get; } = [BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video];

    /// <summary>Gets the library ids in scope and their root paths.</summary>
    /// <returns>Library ids and root folders.</returns>
    public (Guid[] LibraryIds, string[] Roots) GetScope()
    {
        var scope = CurrentScope();
        return (scope.LibraryIds, scope.Roots);
    }

    /// <summary>
    /// Returns true when the item is an eligible file inside a Live TV recording folder. Commercial
    /// removal is restricted to these.
    /// </summary>
    /// <param name="item">Item.</param>
    /// <returns>True for DVR recordings.</returns>
    public bool IsLiveTvRecording(BaseItem? item)
    {
        if (!IsEligible(item))
        {
            return false;
        }

        // Recording folders are looked up even when "Live TV recordings" analysis is off, so a
        // recording in a manually selected library is still recognised as one.
        var roots = CurrentScope().RecordingRoots;
        return roots.Any(root => IsUnder(item!.Path, root));
    }

    private Scope CurrentScope()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cachedAt < _cacheLifetime)
            {
                return _cached;
            }

            _cached = ComputeScope();
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    /// <summary>Forces the scope to be recomputed (after a configuration change).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cachedAt = DateTime.MinValue;
        }
    }

    /// <summary>Returns true when the item is a local video file inside an analyzed library.</summary>
    /// <param name="item">Item.</param>
    /// <returns>True when eligible.</returns>
    public bool IsEligible(BaseItem? item)
    {
        if (item is null
            || item.IsVirtualItem
            || item.IsFolder
            || item.IsShortcut
            || item.MediaType != MediaType.Video
            || !item.IsFileProtocol
            || string.IsNullOrEmpty(item.Path)
            || !ItemKinds.Contains(item.GetBaseItemKind()))
        {
            return false;
        }

        var (_, roots) = GetScope();
        return roots.Any(root => IsUnder(item.Path, root));
    }

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, comparison);
    }

    private Scope ComputeScope()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return new Scope(Array.Empty<Guid>(), Array.Empty<string>(), Array.Empty<string>());
        }

        var selected = new HashSet<string>(config.SelectedLibraryIds.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var allRecordingRoots = GetRecordingRoots();
        var recordingRoots = config.IncludeLiveTvRecordings ? allRecordingRoots : Array.Empty<string>();

        var ids = new List<Guid>();
        var roots = new List<string>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var id))
            {
                continue;
            }

            var locations = folder.Locations ?? [];
            var isSelected = selected.Contains(id.ToString("N"));

            // A recordings library either is the recording folder (or below it), or is a broader
            // library that contains it. In the second case only the recording folder is in scope.
            var isRecordingLibrary = locations.Any(l => recordingRoots.Any(r => SamePathOrUnder(l, r)));
            var containsRecordings = locations.Any(l => recordingRoots.Any(r => SamePathOrUnder(r, l)));
            if (!isSelected && !isRecordingLibrary && !containsRecordings)
            {
                continue;
            }

            ids.Add(id);
            if (isSelected || isRecordingLibrary)
            {
                roots.AddRange(locations.Where(l => !string.IsNullOrWhiteSpace(l)));
            }
        }

        roots.AddRange(recordingRoots);
        return new Scope(ids.ToArray(), roots.Distinct().ToArray(), allRecordingRoots);
    }

    private static bool SamePathOrUnder(string path, string root)
    {
        var a = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || IsUnder(a, b);
    }

    private string[] GetRecordingRoots()
    {
        var roots = new List<string>();
        try
        {
            var options = _config.GetConfiguration<LiveTvOptions>("livetv");
            roots.Add(string.IsNullOrWhiteSpace(options.RecordingPath)
                ? Path.Combine(_config.CommonApplicationPaths.DataPath, "livetv", "recordings")
                : options.RecordingPath);
            if (!string.IsNullOrWhiteSpace(options.MovieRecordingPath))
            {
                roots.Add(options.MovieRecordingPath);
            }

            if (!string.IsNullOrWhiteSpace(options.SeriesRecordingPath))
            {
                roots.Add(options.SeriesRecordingPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live TV configuration unavailable");
            roots.Add(Path.Combine(_config.CommonApplicationPaths.DataPath, "livetv", "recordings"));
        }

        return roots.ToArray();
    }

    private sealed record Scope(Guid[] LibraryIds, string[] Roots, string[] RecordingRoots);

    private static string Normalize(string id) => Guid.TryParse(id, out var g) ? g.ToString("N") : id;
}
