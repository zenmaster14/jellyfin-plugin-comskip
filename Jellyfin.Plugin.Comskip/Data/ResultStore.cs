using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Data;

/// <summary>
/// Stores one small JSON file per analyzed item. Kept deliberately simple: no database,
/// results survive plugin updates, and a user can inspect or delete them by hand.
/// </summary>
public class ResultStore
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly ILogger<ResultStore> _logger;
    private readonly ConcurrentDictionary<Guid, AnalysisResult?> _cache = new();

    /// <summary>Initializes a new instance of the <see cref="ResultStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ResultStore(ILogger<ResultStore> logger)
    {
        _logger = logger;
    }

    private static string Folder
    {
        get
        {
            var root = Plugin.Instance?.StoragePath
                ?? throw new InvalidOperationException("Comskip plugin is not initialized.");
            var folder = System.IO.Path.Combine(root, "results");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>Gets the stored result for an item, or null.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The result or null.</returns>
    public AnalysisResult? Get(Guid itemId)
    {
        return _cache.GetOrAdd(itemId, Load);
    }

    /// <summary>Saves a result.</summary>
    /// <param name="result">The result.</param>
    public void Save(AnalysisResult result)
    {
        var path = FileFor(result.ItemId);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(result, _json));
        File.Move(tmp, path, true);
        _cache[result.ItemId] = result;
    }

    /// <summary>Deletes the stored result for an item.</summary>
    /// <param name="itemId">Item id.</param>
    public void Delete(Guid itemId)
    {
        _cache.TryRemove(itemId, out _);
        try
        {
            File.Delete(FileFor(itemId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete comskip result for {ItemId}", itemId);
        }
    }

    /// <summary>Counts stored results.</summary>
    /// <returns>Number of result files.</returns>
    public int Count()
    {
        try
        {
            return Directory.EnumerateFiles(Folder, "*.json").Count();
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Returns true when a stored result exists and still describes the file on disk
    /// (same size and modification time).
    /// </summary>
    /// <param name="result">Stored result.</param>
    /// <param name="path">Current media path.</param>
    /// <returns>True when up to date.</returns>
    public static bool Matches(AnalysisResult? result, string? path)
    {
        if (result is null || string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists
                && string.Equals(result.Path, path, StringComparison.Ordinal)
                && result.FileSize == info.Length
                && result.LastWriteTicks == info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FileFor(Guid itemId) => System.IO.Path.Combine(Folder, itemId.ToString("N") + ".json");

    private AnalysisResult? Load(Guid itemId)
    {
        var path = FileFor(itemId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(path), _json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable comskip result {Path}", path);
            return null;
        }
    }
}
