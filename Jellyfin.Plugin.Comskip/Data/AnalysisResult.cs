namespace Jellyfin.Plugin.Comskip.Data;

/// <summary>Outcome of the last comskip run for one item.</summary>
public enum AnalysisStatus
{
    /// <summary>Comskip ran and the segments are valid.</summary>
    Success = 0,

    /// <summary>Comskip failed; the item is not retried until the file changes or a forced run.</summary>
    Failed = 1
}

/// <summary>One detected commercial break, in seconds.</summary>
public class CommercialBreak
{
    /// <summary>Gets or sets the start in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Gets or sets the end in seconds.</summary>
    public double End { get; set; }
}

/// <summary>Persisted comskip result for one library item.</summary>
public class AnalysisResult
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the media path that was analyzed.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size at analysis time (change detection).</summary>
    public long FileSize { get; set; }

    /// <summary>Gets or sets the file's last write time (UTC ticks) at analysis time.</summary>
    public long LastWriteTicks { get; set; }

    /// <summary>Gets or sets when the analysis finished.</summary>
    public DateTime AnalyzedAtUtc { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public AnalysisStatus Status { get; set; }

    /// <summary>Gets or sets the failure reason, if any.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets how long comskip took, in seconds.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets the commercial breaks (already padded and filtered).</summary>
    public List<CommercialBreak> Breaks { get; set; } = [];
}
