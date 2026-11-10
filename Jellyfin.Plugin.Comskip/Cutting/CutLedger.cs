using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Cutting;

/// <summary>One recording that has had its commercials removed.</summary>
public class CutEntry
{
    /// <summary>Gets or sets the media path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the size of the cut file.</summary>
    public long FileSize { get; set; }

    /// <summary>Gets or sets the last write time (UTC ticks) of the cut file.</summary>
    public long LastWriteTicks { get; set; }

    /// <summary>Gets or sets when the cut happened.</summary>
    public DateTime CutAtUtc { get; set; }

    /// <summary>Gets or sets the original length in seconds.</summary>
    public double OriginalSeconds { get; set; }

    /// <summary>Gets or sets the length after cutting, in seconds.</summary>
    public double CutSeconds { get; set; }
}

/// <summary>
/// Remembers which files have already been cut. Deliberately kept apart from the analysis results,
/// which Jellyfin prunes when it notices a file changed. Without this record a cut recording would be
/// re-analyzed and could be cut a second time.
/// </summary>
public class CutLedger
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly ILogger<CutLedger> _logger;
    private readonly Lock _lock = new();
    private Dictionary<Guid, CutEntry>? _entries;
    private bool _unreadable;

    /// <summary>Initializes a new instance of the <see cref="CutLedger"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public CutLedger(ILogger<CutLedger> logger)
    {
        _logger = logger;
    }

    private static string FilePath => System.IO.Path.Combine(
        Plugin.Instance?.StoragePath ?? throw new InvalidOperationException("Comskip plugin is not initialized."),
        "cut-ledger.json");

    /// <summary>Gets the number of cut recordings.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return Load().Count;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the ledger could be read. If it is damaged, cutting is
    /// suspended rather than risking a second cut of an already-cut recording.
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            lock (_lock)
            {
                Load();
                return !_unreadable;
            }
        }
    }

    /// <summary>Returns true when this exact file (path, size, mtime) is the output of a cut.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="path">Current path.</param>
    /// <returns>True if already cut.</returns>
    public bool IsCut(Guid itemId, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        CutEntry? entry;
        lock (_lock)
        {
            Load().TryGetValue(itemId, out entry);
        }

        if (entry is null || !string.Equals(entry.Path, path, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == entry.FileSize && info.LastWriteTimeUtc.Ticks == entry.LastWriteTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the file can't be inspected, err on the side of never cutting it again.
            return true;
        }
    }

    /// <summary>Records a cut.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="entry">Entry.</param>
    public void Add(Guid itemId, CutEntry entry)
    {
        lock (_lock)
        {
            Load()[itemId] = entry;
            Save();
        }
    }

    /// <summary>Forgets an item (it was removed from the library).</summary>
    /// <param name="itemId">Item id.</param>
    public void Remove(Guid itemId)
    {
        lock (_lock)
        {
            if (Load().Remove(itemId))
            {
                Save();
            }
        }
    }

    /// <summary>Lists recorded item ids.</summary>
    /// <returns>Ids.</returns>
    public List<Guid> ItemIds()
    {
        lock (_lock)
        {
            return Load().Keys.ToList();
        }
    }

    private Dictionary<Guid, CutEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            _entries = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<Guid, CutEntry>>(File.ReadAllText(FilePath), _json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Comskip: could not read {Path}; commercial removal is suspended until it is fixed or deleted", FilePath);
            _entries = new();
            _unreadable = true;
        }

        return _entries;
    }

    private void Save()
    {
        if (_unreadable)
        {
            return;
        }

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, _json));
        File.Move(tmp, FilePath, true);
    }
}
