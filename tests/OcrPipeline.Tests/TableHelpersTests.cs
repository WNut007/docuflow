using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Pure multi-line-cell collapse rule (LineSelectMode/Indices/Separator).</summary>
public sealed class CellLineSelectorTests
{
    [Fact]
    public void All_joins_non_empty_lines_with_default_space()
        => Assert.Equal("Front and rear brake cables",
            CellLineSelector.Apply(new[] { "Front and rear", "", "brake cables" }, "ALL", null, null));

    [Fact]
    public void All_honors_a_custom_separator()
        => Assert.Equal("a-b", CellLineSelector.Apply(new[] { "a", "b" }, "ALL", null, "-"));

    [Fact]
    public void First_takes_the_first_non_empty_line()
        => Assert.Equal("a", CellLineSelector.Apply(new[] { "", "a", "b" }, "FIRST", null, null));

    [Fact]
    public void Pick_selects_listed_indices()
        => Assert.Equal("a c", CellLineSelector.Apply(new[] { "a", "b", "c" }, "PICK", "0,2", " "));

    [Fact]
    public void Pick_with_no_valid_index_falls_back_to_all()
        => Assert.Equal("a b", CellLineSelector.Apply(new[] { "a", "b" }, "PICK", "9", " "));

    [Fact]
    public void Unknown_or_blank_mode_defaults_to_all()
        => Assert.Equal("a b", CellLineSelector.Apply(new[] { "a", "b" }, null, null, null));

    [Fact]
    public void Empty_input_is_empty_string()
        => Assert.Equal("", CellLineSelector.Apply(System.Array.Empty<string>(), "ALL", null, null));
}

/// <summary>Pure table-cell geometry: column x-range + row band -> pixel sub-rect within the zone.</summary>
public sealed class TableGeometryTests
{
    [Fact]
    public void Page_x_is_made_relative_to_the_zone_and_clamped()
    {
        Assert.Equal(0.5, TableGeometry.ToZoneRelativeX(0.5, 0.4, 0.2), 6); // (0.5-0.4)/0.2
        Assert.Equal(0.0, TableGeometry.ToZoneRelativeX(0.3, 0.4, 0.2), 6); // left of zone -> 0
        Assert.Equal(1.0, TableGeometry.ToZoneRelativeX(0.9, 0.4, 0.2), 6); // right of zone -> 1
        Assert.Equal(0.0, TableGeometry.ToZoneRelativeX(0.5, 0.4, 0.0), 6); // zero-width zone -> 0
    }

    [Fact]
    public void Cell_rect_maps_column_and_band_into_zone_pixels()
    {
        var zone = new PixelRect(100, 200, 400, 300);
        var rect = TableGeometry.CellPixelRect(zone, 0.0, 0.25, new RowBand(0.0, 0.5));
        Assert.Equal(new PixelRect(100, 200, 100, 150), rect);
    }

    [Fact]
    public void Cell_rect_clamps_to_the_zone_bounds()
    {
        var zone = new PixelRect(100, 200, 400, 300);
        var rect = TableGeometry.CellPixelRect(zone, 0.9, 1.0, new RowBand(0.9, 1.0));
        Assert.True(rect.X + rect.Width <= zone.X + zone.Width);
        Assert.True(rect.Y + rect.Height <= zone.Y + zone.Height);
        Assert.True(rect.Width >= 1 && rect.Height >= 1);
    }
}

/// <summary>Pure horizontal ink projection used for row-boundary snapping.</summary>
public sealed class HorizontalInkProfileTests
{
    [Fact]
    public void Counts_dark_pixels_per_scanline()
    {
        const int w = 4, h = 3;
        var luma = new byte[w * h];
        for (int x = 0; x < w; x++) { luma[0 * w + x] = 200; luma[1 * w + x] = 50; luma[2 * w + x] = 200; }
        var profile = ImagePreprocessor.HorizontalInkProfile(luma, w, h);
        Assert.Equal(new[] { 0, 4, 0 }, profile);
    }

    [Fact]
    public void Empty_image_yields_empty_profile()
        => Assert.Empty(ImagePreprocessor.HorizontalInkProfile(System.Array.Empty<byte>(), 0, 0));
}
