using System.Globalization;
using Jellyfin.Plugin.Comskip.Data;

namespace Jellyfin.Plugin.Comskip.Comskip;

/// <summary>Parses comskip EDL output and shapes it into skippable breaks.</summary>
public static class EdlParser
{
    /// <summary>
    /// Parses EDL text ("start end action" per line, seconds). Every line comskip writes is a
    /// commercial, regardless of the action column (0 = cut, 3 = commercial break).
    /// </summary>
    /// <param name="edl">File contents.</param>
    /// <returns>Raw breaks.</returns>
    public static List<CommercialBreak> Parse(string edl)
    {
        var breaks = new List<CommercialBreak>();
        foreach (var rawLine in edl.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
            {
                continue;
            }

            if (end > start && start >= 0)
            {
                breaks.Add(new CommercialBreak { Start = start, End = end });
            }
        }

        return breaks;
    }

    /// <summary>Merges overlaps, applies padding, drops short breaks and clamps to the runtime.</summary>
    /// <param name="breaks">Raw breaks.</param>
    /// <param name="startPadding">Seconds to move each start later.</param>
    /// <param name="endPadding">Seconds to move each end earlier.</param>
    /// <param name="minSeconds">Minimum break length kept.</param>
    /// <param name="runtimeSeconds">Item runtime, if known.</param>
    /// <returns>Final breaks, ordered.</returns>
    public static List<CommercialBreak> Shape(
        IEnumerable<CommercialBreak> breaks,
        double startPadding,
        double endPadding,
        double minSeconds,
        double? runtimeSeconds)
    {
        var merged = new List<CommercialBreak>();
        foreach (var b in breaks.OrderBy(b => b.Start))
        {
            // Comskip sometimes emits back-to-back blocks a frame or two apart; join them.
            if (merged.Count > 0 && b.Start <= merged[^1].End + 1.0)
            {
                merged[^1].End = Math.Max(merged[^1].End, b.End);
            }
            else
            {
                merged.Add(new CommercialBreak { Start = b.Start, End = b.End });
            }
        }

        var result = new List<CommercialBreak>();
        foreach (var b in merged)
        {
            var start = Math.Max(0, b.Start + startPadding);
            var end = b.End - endPadding;
            if (runtimeSeconds is > 0)
            {
                end = Math.Min(end, runtimeSeconds.Value);
            }

            if (end - start >= Math.Max(1, minSeconds))
            {
                result.Add(new CommercialBreak { Start = Math.Round(start, 3), End = Math.Round(end, 3) });
            }
        }

        return result;
    }

    /// <summary>Formats breaks as a Kodi/MPV EDL (action 3 = commercial break).</summary>
    /// <param name="breaks">Breaks.</param>
    /// <returns>EDL text.</returns>
    public static string ToEdl(IEnumerable<CommercialBreak> breaks)
    {
        return string.Concat(breaks.Select(b => string.Create(
            CultureInfo.InvariantCulture,
            $"{b.Start:0.000}\t{b.End:0.000}\t3\n")));
    }
}
