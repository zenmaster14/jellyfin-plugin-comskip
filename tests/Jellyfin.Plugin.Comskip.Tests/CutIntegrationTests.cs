using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Comskip.Cutting;
using Jellyfin.Plugin.Comskip.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Comskip.Tests;

/// <summary>
/// Cuts synthetic "recordings" with real ffmpeg: show = moving test pattern + tone, commercial =
/// black + silence. Afterwards almost no black may remain, the length must match and audio must
/// stay in step with video.
/// </summary>
public class CutIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "comskip-cut-" + Guid.NewGuid().ToString("N"));

    public CutIntegrationTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public static TheoryData<string, string, string, double> Formats => new()
    {
        // name, video codec args, audio codec args, GOP seconds
        { "mpeg2_ac3", "-c:v mpeg2video -b:v 3M -g 15", "-c:a ac3", 0.5 },
        { "h264_aac", "-c:v libx264 -preset ultrafast -g 60", "-c:a aac", 2.0 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task RemovesCommercials(string name, string videoArgs, string audioArgs, double gopSeconds)
    {
        // show 60s | ad 30s | show 45s | ad 20s | show 30s  = 185s, 135s of show
        var plan = new (bool Ad, int Seconds)[] { (false, 60), (true, 30), (false, 45), (true, 20), (false, 30) };
        var input = Path.Combine(_dir, name + ".ts");
        MakeRecording(input, plan, videoArgs, audioArgs);

        var breaks = new List<CommercialBreak> { new() { Start = 60, End = 90 }, new() { Start = 135, End = 155 } };
        var cutter = new TsCutter(NullLogger.Instance);
        var duration = await cutter.ProbeDurationAsync("ffprobe", input, CancellationToken.None);
        Assert.NotNull(duration);
        _out.WriteLine($"input duration {duration:0.00}");

        var keeps = TsCutter.ComputeKeepSegments(breaks, duration!.Value);
        var output = Path.Combine(_dir, name + ".cut.ts");
        var result = await cutter.CutAsync("ffmpeg", "ffprobe", input, keeps, Path.Combine(_dir, name + "-work"), output, false, CancellationToken.None);
        _out.WriteLine($"expected {result.ExpectedSeconds:0.00} actual {result.ActualSeconds:0.00} error {result.Error}");

        Assert.True(result.Success, result.Error);
        Assert.InRange(result.ActualSeconds, 134, 135 + (2 * gopSeconds) + 1.5);

        // At most one GOP of each removed ad may remain at the start of the following part.
        var black = Detect(output, "-vf blackdetect=d=0.1:pix_th=0.10 -an", @"black_duration:\s*([0-9.]+)");
        _out.WriteLine($"black remaining {black:0.00}s");
        Assert.True(black <= (2 * gopSeconds) + 0.6, $"{black:0.00}s of commercial remained");

        // Audio and video must end together (no drift introduced by the joins).
        var (video, audio) = StreamDurations(output);
        _out.WriteLine($"video {video:0.00}s audio {audio:0.00}s");
        Assert.True(Math.Abs(video - audio) < 0.5, $"A/V length mismatch: video {video:0.00}s, audio {audio:0.00}s");
    }

    [Fact]
    public async Task FailsCleanlyOnMissingInput()
    {
        var cutter = new TsCutter(NullLogger.Instance);
        var output = Path.Combine(_dir, "none.cut.ts");
        var result = await cutter.CutAsync("ffmpeg", "ffprobe", Path.Combine(_dir, "missing.ts"), [new KeepSegment(0, 10)], Path.Combine(_dir, "w"), output, false, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(File.Exists(output));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static void MakeRecording(string path, (bool Ad, int Seconds)[] plan, string videoArgs, string audioArgs)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-y" };
        var filter = new System.Text.StringBuilder();
        for (var i = 0; i < plan.Length; i++)
        {
            var (ad, seconds) = plan[i];
            var video = ad ? $"color=c=black:s=640x360:r=30000/1001:d={seconds}" : $"testsrc2=s=640x360:r=30000/1001:d={seconds}";
            var audio = ad ? $"sine=f=440:d={seconds},volume=0" : $"sine=f={300 + (i * 100)}:d={seconds}";
            args.AddRange(["-f", "lavfi", "-i", video, "-f", "lavfi", "-i", audio]);
            filter.Append(CultureInfo.InvariantCulture, $"[{2 * i}:v][{(2 * i) + 1}:a]");
        }

        filter.Append(CultureInfo.InvariantCulture, $"concat=n={plan.Length}:v=1:a=1[v][a]");
        args.AddRange(["-filter_complex", filter.ToString(), "-map", "[v]", "-map", "[a]"]);
        args.AddRange(videoArgs.Split(' '));
        args.AddRange(audioArgs.Split(' '));
        args.AddRange(["-f", "mpegts", path]);
        var (code, _, err) = Run("ffmpeg", args);
        Assert.True(code == 0, err);
    }

    private static double Detect(string path, string filterArgs, string pattern)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-i", path };
        args.AddRange(filterArgs.Split(' '));
        args.AddRange(["-f", "null", "-"]);
        var (_, _, err) = Run("ffmpeg", args);
        return Regex.Matches(err, pattern).Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static (double Video, double Audio) StreamDurations(string path)
    {
        var (_, stdout, _) = Run("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type,duration", "-of", "csv=p=0", path]);
        double video = 0, audio = 0;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                continue;
            }

            if (parts[0] == "video")
            {
                video = d;
            }
            else if (parts[0] == "audio")
            {
                audio = d;
            }
        }

        return (video, audio);
    }

    private static (int Code, string Stdout, string Stderr) Run(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }
}
