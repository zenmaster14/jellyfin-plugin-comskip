using System.Globalization;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Comskip.Configuration;
using Jellyfin.Plugin.Comskip.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Comskip;

/// <summary>Result of one comskip invocation.</summary>
/// <param name="Success">Whether comskip produced usable output.</param>
/// <param name="Breaks">Raw breaks (unshaped).</param>
/// <param name="Error">Error message when unsuccessful.</param>
public sealed record ComskipRun(bool Success, List<CommercialBreak> Breaks, string? Error);

/// <summary>
/// Runs comskip for one file in an isolated working folder. Ports the robustness tricks of
/// the original post-processor script: optional sanitizing remux of broken OTA streams
/// (comskip segfaults on corrupt packets), a guaranteed ini, and never touching the
/// original media file.
/// </summary>
public class ComskipRunner
{
    // Keys the plugin controls; any value in the user's ini is replaced.
    private static readonly string[] _forcedKeys =
    [
        "output_edl", "output_ffmeta", "output_txt", "live_tv", "hardware_decode", "thread_count"
    ];

    private static readonly string[] _transportStreamExtensions = [".ts", ".m2ts", ".mts", ".tp", ".trp"];

    private readonly ILogger<ComskipRunner> _logger;
    private readonly ToolLocator _tools;

    /// <summary>Initializes a new instance of the <see cref="ComskipRunner"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="tools">Tool locator.</param>
    public ComskipRunner(ILogger<ComskipRunner> logger, ToolLocator tools)
    {
        _logger = logger;
        _tools = tools;
    }

    /// <summary>Loads the bundled default comskip.ini.</summary>
    /// <returns>Ini contents.</returns>
    public static string LoadBundledIni()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("default-comskip.ini", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Runs comskip against <paramref name="mediaPath"/>.</summary>
    /// <param name="mediaPath">Media file.</param>
    /// <param name="workDir">Empty scratch folder owned by this run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run result.</returns>
    public async Task<ComskipRun> RunAsync(string mediaPath, string workDir, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var comskip = _tools.FindComskip();
        if (comskip is null)
        {
            return new ComskipRun(false, [], "comskip executable not found. Install comskip or set its path in the plugin settings.");
        }

        Directory.CreateDirectory(workDir);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(5, config.TimeoutMinutes)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;

        try
        {
            var input = mediaPath;
            if (ShouldSanitize(config.Sanitize, mediaPath))
            {
                input = await SanitizeAsync(mediaPath, workDir, token).ConfigureAwait(false) ?? mediaPath;
            }

            var iniPath = Path.Combine(workDir, "comskip.ini");
            await File.WriteAllTextAsync(iniPath, BuildIni(config), token).ConfigureAwait(false);

            var args = new List<string> { "--ini=" + iniPath, "--output=" + workDir, input };
            var (exitCode, tail) = await ProcessRunner.RunAsync(comskip, args, workDir, config.LowPriority, _logger, token).ConfigureAwait(false);

            var edl = Directory.EnumerateFiles(workDir, "*.edl").FirstOrDefault();
            if (edl is not null)
            {
                var text = await File.ReadAllTextAsync(edl, token).ConfigureAwait(false);
                return new ComskipRun(true, EdlParser.Parse(text), null);
            }

            // Comskip exits 1 when it finds no commercials and may skip writing output.
            if (exitCode is 0 or 1)
            {
                return new ComskipRun(true, [], null);
            }

            return new ComskipRun(false, [], string.Create(CultureInfo.InvariantCulture, $"comskip exited with code {exitCode}: {tail}"));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new ComskipRun(false, [], string.Create(CultureInfo.InvariantCulture, $"Timed out after {config.TimeoutMinutes} minutes."));
        }
    }

    private static bool ShouldSanitize(SanitizeMode mode, string path) => mode switch
    {
        SanitizeMode.Always => true,
        SanitizeMode.TransportStreamsOnly => _transportStreamExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase),
        _ => false
    };

    private static string BuildIni(PluginConfiguration config)
    {
        string baseIni;
        if (!string.IsNullOrWhiteSpace(config.ComskipIniPath) && File.Exists(config.ComskipIniPath))
        {
            baseIni = File.ReadAllText(config.ComskipIniPath);
        }
        else
        {
            baseIni = LoadBundledIni();
        }

        var sb = new StringBuilder();
        foreach (var line in baseIni.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var eq = trimmed.IndexOf('=', StringComparison.Ordinal);
            var key = eq > 0 ? trimmed[..eq].Trim() : string.Empty;
            if (_forcedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.Append(line.TrimEnd('\r')).Append('\n');
        }

        sb.Append("\n; ---- forced by Jellyfin Comskip plugin ----\n");
        sb.Append("output_edl=1\n");
        sb.Append("output_ffmeta=0\n");
        sb.Append("output_txt=0\n");
        sb.Append("live_tv=0\n");
        sb.Append(CultureInfo.InvariantCulture, $"hardware_decode={(config.HardwareDecode ? 1 : 0)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"thread_count={Math.Clamp(config.ComskipThreads, 1, 32)}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Stream-copies the first video and audio track into a clean transport stream, dropping
    /// corrupt packets, regenerating timestamps and discarding data streams (SCTE-35).
    /// Returns null (analyze the original) if ffmpeg is missing or fails.
    /// </summary>
    private async Task<string?> SanitizeAsync(string mediaPath, string workDir, CancellationToken token)
    {
        var ffmpeg = _tools.FindFfmpeg();
        if (ffmpeg is null)
        {
            _logger.LogWarning("ffmpeg not found; analyzing {Path} without sanitizing", mediaPath);
            return null;
        }

        var output = Path.Combine(workDir, "input.ts");
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-err_detect", "ignore_err", "-fflags", "+genpts+discardcorrupt",
            "-i", mediaPath,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-c", "copy", "-dn", "-sn",
            "-f", "mpegts", output
        };

        var (exitCode, tail) = await ProcessRunner.RunAsync(ffmpeg, args, workDir, true, _logger, token).ConfigureAwait(false);
        if (exitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
        {
            return output;
        }

        _logger.LogWarning("Sanitizing remux failed for {Path} (exit {Code}): {Tail}. Analyzing the original instead.", mediaPath, exitCode, tail);
        return null;
    }
}
