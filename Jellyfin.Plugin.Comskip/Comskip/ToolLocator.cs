using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Comskip.Comskip;

/// <summary>Finds the comskip and ffmpeg executables.</summary>
public class ToolLocator
{
    private static readonly string[] _wellKnownComskip =
    [
        "/usr/bin/comskip",
        "/usr/local/bin/comskip",
        "/opt/comskip/comskip",
        "/config/comskip/comskip",
        @"C:\Program Files\Comskip\comskip.exe",
        @"C:\Comskip\comskip.exe"
    ];

    private readonly IMediaEncoder _mediaEncoder;

    /// <summary>Initializes a new instance of the <see cref="ToolLocator"/> class.</summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder (for its ffmpeg path).</param>
    public ToolLocator(IMediaEncoder mediaEncoder)
    {
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>Finds comskip: configured path, then PATH, then well-known locations.</summary>
    /// <returns>Full path or null.</returns>
    public string? FindComskip()
    {
        var configured = Plugin.Instance?.Configuration.ComskipPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var exe = OperatingSystem.IsWindows() ? "comskip.exe" : "comskip";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; ignore.
            }
        }

        return _wellKnownComskip.FirstOrDefault(File.Exists);
    }

    /// <summary>Finds ffmpeg: configured path, else Jellyfin's ffmpeg.</summary>
    /// <returns>Full path or null.</returns>
    public string? FindFfmpeg()
    {
        var configured = Plugin.Instance?.Configuration.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var jellyfinFfmpeg = _mediaEncoder.EncoderPath;
        return !string.IsNullOrEmpty(jellyfinFfmpeg) && File.Exists(jellyfinFfmpeg) ? jellyfinFfmpeg : null;
    }
}
