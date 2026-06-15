using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Pure-geometry tests: normalized 0..1 zone rectangle -> pixel crop, with clamping.</summary>
public sealed class ZoneGeometryTests
{
    [Fact]
    public void Maps_normalized_rect_to_pixels()
    {
        // invoice-number zone on the 750x1061 east-repair sample
        var r = ZoneGeometry.ToPixelRect(0.840m, 0.227m, 0.090m, 0.022m, 750, 1061);
        Assert.Equal(630, r.X);   // round(0.840*750)
        Assert.Equal(241, r.Y);   // round(0.227*1061)
        Assert.Equal(68, r.Width);  // round(0.090*750)
        Assert.Equal(23, r.Height); // round(0.022*1061)
    }

    [Fact]
    public void Clamps_width_and_height_to_image_bounds()
    {
        var r = ZoneGeometry.ToPixelRect(0.95m, 0.95m, 0.20m, 0.20m, 100, 100);
        Assert.Equal(95, r.X);
        Assert.Equal(95, r.Y);
        Assert.Equal(5, r.Width);   // 100-95
        Assert.Equal(5, r.Height);
    }

    [Fact]
    public void Guarantees_non_empty_rectangle()
    {
        var r = ZoneGeometry.ToPixelRect(0.5m, 0.5m, 0m, 0m, 100, 100);
        Assert.True(r.Width >= 1 && r.Height >= 1);

        var z = ZoneGeometry.ToPixelRect(0.5m, 0.5m, 0.1m, 0.1m, 0, 0);
        Assert.Equal(new PixelRect(0, 0, 1, 1), z);   // zero-sized page -> safe 1x1
    }
}

/// <summary>Pure hint mapping: OCR hint + optional PSM override -> (PageSegMode, whitelist).</summary>
public sealed class ZoneHintTests
{
    [Theory]
    [InlineData("NUMERIC", "0123456789.,-")]
    [InlineData("date", "0123456789/.-")]   // case-insensitive
    [InlineData("INT", "0123456789")]
    public void Maps_hint_to_whitelist(string hint, string expected)
    {
        var (psm, whitelist) = ZoneHint.Resolve(hint, null);
        Assert.Equal(7, psm);                 // single line by default
        Assert.Equal(expected, whitelist);
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData(null)]
    [InlineData("something-else")]
    public void Text_or_unknown_has_no_whitelist(string? hint)
    {
        var (psm, whitelist) = ZoneHint.Resolve(hint, null);
        Assert.Equal(7, psm);
        Assert.Null(whitelist);
    }

    [Fact]
    public void Psm_override_wins_when_positive()
    {
        Assert.Equal(6, ZoneHint.Resolve("NUMERIC", 6).Psm);
        Assert.Equal(7, ZoneHint.Resolve("TEXT", 0).Psm);   // 0 is ignored -> default single line
    }
}
