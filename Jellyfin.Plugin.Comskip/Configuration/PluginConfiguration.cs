using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Comskip.Configuration;

/// <summary>When to remux the input to a clean transport stream before running comskip.</summary>
public enum SanitizeMode
{
    /// <summary>Always analyze the original file.</summary>
    Never = 0,

    /// <summary>Sanitize .ts / .m2ts / .mts recordings only (OTA streams are the usual source of comskip crashes).</summary>
    TransportStreamsOnly = 1,

    /// <summary>Sanitize every file.</summary>
    Always = 2
}

/// <summary>Plugin configuration.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---- Scope -----------------------------------------------------------------

    /// <summary>Gets or sets a value indicating whether Live TV recording folders are analyzed.</summary>
    public bool IncludeLiveTvRecordings { get; set; } = true;

    /// <summary>Gets or sets the ids (no dashes) of additional libraries to analyze.</summary>
    public string[] SelectedLibraryIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether new and changed items are analyzed automatically.</summary>
    public bool AutoAnalyzeNewItems { get; set; } = true;

    // ---- Tools -----------------------------------------------------------------

    /// <summary>Gets or sets an explicit path to the comskip binary. Empty = search PATH.</summary>
    public string ComskipPath { get; set; } = string.Empty;

    /// <summary>Gets or sets an explicit path to ffmpeg. Empty = use Jellyfin's own ffmpeg.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a custom comskip.ini. Empty = use the bundled tuned ini.</summary>
    public string ComskipIniPath { get; set; } = string.Empty;

    // ---- Processing ------------------------------------------------------------

    /// <summary>Gets or sets when to remux the input before analysis.</summary>
    public SanitizeMode Sanitize { get; set; } = SanitizeMode.TransportStreamsOnly;

    /// <summary>Gets or sets a value indicating whether comskip uses hardware-assisted decoding.</summary>
    public bool HardwareDecode { get; set; } = true;

    /// <summary>Gets or sets the number of threads comskip uses.</summary>
    public int ComskipThreads { get; set; } = 2;

    /// <summary>Gets or sets the number of files analyzed at the same time. Restart Jellyfin to apply.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether comskip runs at below-normal priority.</summary>
    public bool LowPriority { get; set; } = true;

    /// <summary>Gets or sets the per-file timeout in minutes.</summary>
    public int TimeoutMinutes { get; set; } = 240;

    /// <summary>Gets or sets how long (seconds) a file must be unmodified before it is analyzed.</summary>
    public int SettleSeconds { get; set; } = 120;

    // ---- Segment shaping -------------------------------------------------------

    /// <summary>Gets or sets the minimum commercial break length to report, in seconds.</summary>
    public int MinSegmentSeconds { get; set; } = 15;

    /// <summary>Gets or sets seconds trimmed from the start of each break (positive = skip starts later).</summary>
    public double StartPaddingSeconds { get; set; }

    /// <summary>Gets or sets seconds trimmed from the end of each break (positive = skip ends earlier).</summary>
    public double EndPaddingSeconds { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether a Kodi/MPV-style .edl file is written next to the media.</summary>
    public bool WriteEdlSidecar { get; set; }

    // ---- Commercial removal (2.0) ------------------------------------------------

    /// <summary>
    /// Gets or sets a value indicating whether detected commercials are permanently cut out of Live TV
    /// recordings (.ts), replacing the original file. No backup is kept.
    /// </summary>
    public bool RemoveCommercials { get; set; }

    /// <summary>Gets or sets the largest share of a recording (percent) that may be removed; above this the file is left alone.</summary>
    public int CutMaxRemovedPercent { get; set; } = 50;

    /// <summary>Gets or sets the shortest result (minutes) a cut may leave; below this the file is left alone.</summary>
    public int CutMinRemainingMinutes { get; set; } = 5;
}
