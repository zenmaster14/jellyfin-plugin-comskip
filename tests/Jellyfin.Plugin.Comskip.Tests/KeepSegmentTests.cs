using Jellyfin.Plugin.Comskip.Comskip;
using Jellyfin.Plugin.Comskip.Cutting;
using Jellyfin.Plugin.Comskip.Data;
using Xunit;

namespace Jellyfin.Plugin.Comskip.Tests;

public class KeepSegmentTests
{
    private static CommercialBreak B(double s, double e) => new() { Start = s, End = e };

    [Fact]
    public void KeepsEverythingBetweenBreaks()
    {
        var keeps = TsCutter.ComputeKeepSegments([B(60, 90), B(135, 155)], 185);
        Assert.Equal(new[] { new KeepSegment(0, 60), new KeepSegment(90, 135), new KeepSegment(155, 185) }, keeps);
    }

    [Fact]
    public void BreakAtStartAndEnd()
    {
        var keeps = TsCutter.ComputeKeepSegments([B(0, 30), B(170, 200)], 185);
        Assert.Equal(new[] { new KeepSegment(30, 170) }, keeps);
    }

    [Fact]
    public void OverlappingAndUnorderedBreaksMerge()
    {
        var keeps = TsCutter.ComputeKeepSegments([B(100, 120), B(50, 80), B(70, 110)], 200);
        Assert.Equal(new[] { new KeepSegment(0, 50), new KeepSegment(120, 200) }, keeps);
    }

    [Fact]
    public void DropsSlivers()
    {
        var keeps = TsCutter.ComputeKeepSegments([B(10, 50), B(51, 90)], 120);
        Assert.Equal(new[] { new KeepSegment(0, 10), new KeepSegment(90, 120) }, keeps);
    }

    [Fact]
    public void NoBreaksKeepsWholeFile()
    {
        Assert.Equal(new[] { new KeepSegment(0, 60) }, TsCutter.ComputeKeepSegments([], 60));
    }

    [Fact]
    public void EdlParsesAndShapes()
    {
        var raw = EdlParser.Parse("60.00\t90.00\t0\n135.5 155.2 3\n# comment\nbad line\n");
        Assert.Equal(2, raw.Count);
        var shaped = EdlParser.Shape(raw, 0, 1, 15, null);
        Assert.Equal(60, shaped[0].Start);
        Assert.Equal(89, shaped[0].End);
    }
}
