using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Comskip.Comskip;
using Jellyfin.Plugin.Comskip.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Cutting;

/// <summary>A part of the recording to keep, in seconds from the start of the file.</summary>
/// <param name="Start">Start.</param>
/// <param name="End">End.</param>
public readonly record struct KeepSegment(double Start, double End)
{
    /// <summary>Gets the length in seconds.</summary>
    public double Length => End - Start;
}

/// <summary>Result of a cut.</summary>
/// <param name="Success">Whether the output file is valid.</param>
/// <param name="Error">Failure reason.</param>
/// <param name="ExpectedSeconds">Planned output length.</param>
/// <param name="ActualSeconds">Probed output length.</param>
public sealed record CutResult(bool Success, string? Error, double ExpectedSeconds, double ActualSeconds);

/// <summary>
/// Removes commercial breaks from an MPEG transport stream without re-encoding: every part to keep
/// is stream-copied into its own file, then the parts are joined with FFmpeg's concat demuxer.
/// Stream copy can only start a part on a keyframe, so each part starts at the keyframe at or before
/// the requested time. Up to one GOP (typically 0.5-2 s) of the preceding break can remain, and
/// show content is never lost. Has no Jellyfin dependencies so it can be tested on its own.
/// </summary>
public sealed class TsCutter
{
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="TsCutter"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public TsCutter(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Computes the parts to keep: everything that is not a commercial break.</summary>
    /// <param name="breaks">Commercial breaks (any order, may overlap).</param>
    /// <param name="duration">Total file length in seconds.</param>
    /// <param name="minKeepSeconds">Kept fragments shorter than this are dropped.</param>
    /// <returns>Ordered parts to keep.</returns>
    public static List<KeepSegment> ComputeKeepSegments(IEnumerable<CommercialBreak> breaks, double duration, double minKeepSeconds = 2)
    {
        var keeps = new List<KeepSegment>();
        var cursor = 0.0;
        foreach (var b in breaks.Where(b => b.End > b.Start).OrderBy(b => b.Start))
        {
            var start = Math.Clamp(b.Start, 0, duration);
            var end = Math.Clamp(b.End, 0, duration);
            if (start > cursor)
            {
                keeps.Add(new KeepSegment(cursor, start));
            }

            cursor = Math.Max(cursor, end);
        }

        if (duration > cursor)
        {
            keeps.Add(new KeepSegment(cursor, duration));
        }

        return keeps.Where(k => k.Length >= minKeepSeconds).ToList();
    }

    /// <summary>Reads a file's duration with ffprobe.</summary>
    /// <param name="ffprobe">ffprobe path.</param>
    /// <param name="path">Media file.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>Seconds, or null if unknown.</returns>
    public async Task<double?> ProbeDurationAsync(string ffprobe, string path, CancellationToken token)
    {
        var (code, output) = await ProcessRunner.RunAsync(
            ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path],
            Path.GetDirectoryName(path) ?? ".",
            false,
            _logger,
            token,
            captureAll: true).ConfigureAwait(false);

        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return code == 0 && double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : null;
    }

    /// <summary>Writes <paramref name="outputPath"/> containing only <paramref name="keeps"/> of <paramref name="inputPath"/>.</summary>
    /// <param name="ffmpeg">ffmpeg path.</param>
    /// <param name="ffprobe">ffprobe path.</param>
    /// <param name="inputPath">Original recording.</param>
    /// <param name="keeps">Parts to keep, ordered.</param>
    /// <param name="workDir">Scratch folder for the parts (deleted by the caller).</param>
    /// <param name="outputPath">Where to write the result.</param>
    /// <param name="lowPriority">Run ffmpeg at low priority.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns>The result; on failure the output file is removed.</returns>
    public async Task<CutResult> CutAsync(
        string ffmpeg,
        string ffprobe,
        string inputPath,
        IReadOnlyList<KeepSegment> keeps,
        string workDir,
        string outputPath,
        bool lowPriority,
        CancellationToken token)
    {
        var expected = keeps.Sum(k => k.Length);
        if (keeps.Count == 0)
        {
            return new CutResult(false, "Nothing to keep.", 0, 0);
        }

        Directory.CreateDirectory(workDir);
        var list = new StringBuilder("ffconcat version 1.0\n");

        for (var i = 0; i < keeps.Count; i++)
        {
            var part = string.Create(CultureInfo.InvariantCulture, $"part_{i:D3}.ts");
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error" };
            if (keeps[i].Start > 0)
            {
                args.AddRange(["-ss", Seconds(keeps[i].Start)]);
            }

            args.AddRange(["-i", inputPath, "-t", Seconds(keeps[i].Length)]);
            args.AddRange(CopyArgs);
            args.AddRange(["-avoid_negative_ts", "make_zero", "-f", "mpegts", Path.Combine(workDir, part)]);

            var (code, tail) = await ProcessRunner.RunAsync(ffmpeg, args, workDir, lowPriority, _logger, token).ConfigureAwait(false);
            var partInfo = new FileInfo(Path.Combine(workDir, part));
            if (code != 0 || !partInfo.Exists || partInfo.Length == 0)
            {
                return new CutResult(false, string.Create(CultureInfo.InvariantCulture, $"Extracting part {i + 1} failed (exit {code}): {tail}"), expected, 0);
            }

            list.Append("file '").Append(part).Append("'\n");
        }

        var listPath = Path.Combine(workDir, "parts.ffconcat");
        await File.WriteAllTextAsync(listPath, list.ToString(), token).ConfigureAwait(false);

        var concatArgs = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-map", "0", "-c", "copy", "-f", "mpegts", outputPath
        };
        var (concatCode, concatTail) = await ProcessRunner.RunAsync(ffmpeg, concatArgs, workDir, lowPriority, _logger, token).ConfigureAwait(false);
        if (concatCode != 0 || !File.Exists(outputPath))
        {
            TryDelete(outputPath);
            return new CutResult(false, string.Create(CultureInfo.InvariantCulture, $"Joining parts failed (exit {concatCode}): {concatTail}"), expected, 0);
        }

        var actual = await ProbeDurationAsync(ffprobe, outputPath, token).ConfigureAwait(false) ?? 0;

        // Each part may start up to one GOP early; allow for that plus probing slack.
        var tolerance = 3 + (2.5 * keeps.Count);
        if (actual <= 0 || Math.Abs(actual - expected) > tolerance)
        {
            TryDelete(outputPath);
            return new CutResult(
                false,
                string.Create(CultureInfo.InvariantCulture, $"Output length {actual:0.0}s does not match the expected {expected:0.0}s."),
                expected,
                actual);
        }

        return new CutResult(true, null, expected, actual);
    }

    /// <summary>Gets the stream selection used for every part: all video, audio and subtitle streams;
    /// data streams (SCTE-35 cue messages) are dropped. Closed captions live inside the video
    /// stream and are kept.</summary>
    private static string[] CopyArgs { get; } =
        ["-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-dn", "-ignore_unknown", "-c", "copy"];

    private static string Seconds(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
