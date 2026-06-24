using OcrPipeline.Tests.Fixtures;
using OcrPipeline.Web.Services.Zonal;
using Xunit;
using Cell = OcrPipeline.Web.Services.Zonal.TableLayoutGeometry.CellBox;

namespace OcrPipeline.Tests;

/// <summary>
/// Tests for the pure Option ③-B column-detection core. The headline test runs against the REAL
/// captured Michelin page-1 cell geometry (Fixtures/MichelinStructureCapture.cs — full-page PP-Structure
/// output, header/address/totals noise and all) and asserts the column structure is recovered. The
/// crop→page transform — the one bit of real coordinate math — is unit-tested in isolation.
/// </summary>
public sealed class TableLayoutGeometryTests
{
    private static Cell[] MichelinPage1() =>
        MichelinStructureCapture.Page1CellsNorm
            .Select(a => new Cell(a[0], a[1], a[2], a[3]))
            .ToArray();

    // -------- column detection on REAL captured geometry --------

    [Fact]
    public void DetectColumnBoundaries_recovers_the_Michelin_columns_from_full_page_capture()
    {
        var boundaries = TableLayoutGeometry.DetectColumnBoundaries(MichelinPage1());

        // 5 strong column clusters were found on the full page (left edges ~3/22/44/67/86%), so 4 interior
        // separators. They must be ascending, strictly inside (0,1), and land in the inter-column gaps.
        Assert.Equal(4, boundaries.Count);
        for (int i = 0; i < boundaries.Count; i++)
        {
            Assert.InRange(boundaries[i], 0.0, 1.0);
            if (i > 0) Assert.True(boundaries[i] > boundaries[i - 1], "separators must be ascending");
        }

        // Expected gap midpoints between the captured column right-extents and the next column's start
        // (col edges L≈3/22/44/67/86%, R≈12/35/54/76/93%): ~16.9 / 39.6 / 60.9 / 81.3%.
        double[] expected = { 0.169, 0.396, 0.609, 0.813 };
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(boundaries[i] - expected[i]) <= 0.03,
                $"separator {i} = {boundaries[i]:F3}, expected ~{expected[i]:F3}");
    }

    [Fact]
    public void DetectColumnBoundaries_separators_fall_in_whitespace_not_through_a_column()
    {
        // Each separator must sit clear of every column's content: not within the merge tolerance of a
        // detected left-edge cluster (a column start). Guards against the "left-midpoint cuts through the
        // next column" failure mode.
        double[] columnStarts = { 0.029, 0.218, 0.443, 0.675, 0.860 };
        var boundaries = TableLayoutGeometry.DetectColumnBoundaries(MichelinPage1());
        foreach (var b in boundaries)
            foreach (var s in columnStarts)
                Assert.True(Math.Abs(b - s) > 0.02, $"separator {b:F3} sits on a column start {s:F3}");
    }

    // -------- clean synthetic grid (deterministic, no real-data fuzz) --------

    [Fact]
    public void DetectColumnBoundaries_finds_N_minus_1_separators_for_an_aligned_grid()
    {
        // 6 columns at fixed x-bands, 10 rows each. Content fills [start, start+0.10] of each band; gaps
        // between bands are clean. Expect 5 separators at the gap midpoints.
        double[] starts = { 0.02, 0.20, 0.38, 0.56, 0.74, 0.88 };
        var cells = new List<Cell>();
        for (int row = 0; row < 10; row++)
        {
            double y = row * 0.08;
            foreach (var s in starts) cells.Add(new Cell(s, y, s + 0.10, y + 0.05));
        }

        var b = TableLayoutGeometry.DetectColumnBoundaries(cells);

        Assert.Equal(starts.Length - 1, b.Count);
        for (int i = 0; i < b.Count; i++)
        {
            double expected = (starts[i] + 0.10 + starts[i + 1]) / 2.0;   // (this col's right + next col's start)/2
            Assert.True(Math.Abs(b[i] - expected) <= 0.005, $"sep {i} = {b[i]:F3}, expected {expected:F3}");
        }
    }

    [Fact]
    public void DetectColumnBoundaries_returns_empty_when_under_two_columns()
    {
        Assert.Empty(TableLayoutGeometry.DetectColumnBoundaries(Array.Empty<Cell>()));
        // a single column (one x-band) → no interior separators
        var oneCol = Enumerable.Range(0, 8).Select(r => new Cell(0.1, r * 0.1, 0.3, r * 0.1 + 0.05)).ToArray();
        Assert.Empty(TableLayoutGeometry.DetectColumnBoundaries(oneCol));
    }

    [Fact]
    public void DetectColumnBoundaries_ignores_sparse_noise_below_support()
    {
        // Two real columns (12 rows each) plus a handful of stray cells at random x — the strays are below
        // MinSupport and must not become columns.
        var cells = new List<Cell>();
        for (int r = 0; r < 12; r++) { cells.Add(new Cell(0.10, r * 0.05, 0.25, r * 0.05 + 0.03)); cells.Add(new Cell(0.60, r * 0.05, 0.80, r * 0.05 + 0.03)); }
        cells.Add(new Cell(0.42, 0.01, 0.48, 0.04));   // stray 1
        cells.Add(new Cell(0.92, 0.50, 0.97, 0.53));   // stray 2

        var b = TableLayoutGeometry.DetectColumnBoundaries(cells);

        Assert.Single(b);                              // 2 columns → 1 separator, strays ignored
        Assert.True(b[0] > 0.25 && b[0] < 0.60, $"separator {b[0]:F3} not in the gap between the two columns");
    }

    // -------- crop → page coordinate transform (the real math) --------

    [Theory]
    [InlineData(0.0, 0.10, 0.80, 0.10)]    // crop left edge → zone left
    [InlineData(1.0, 0.10, 0.80, 0.90)]    // crop right edge → zone right (0.10 + 0.80)
    [InlineData(0.5, 0.10, 0.80, 0.50)]    // crop middle → 0.10 + 0.5*0.80
    [InlineData(0.25, 0.20, 0.40, 0.30)]   // 0.20 + 0.25*0.40
    public void CropXToPageX_maps_crop_fraction_into_the_zone(double cropX, double zoneX, double zoneW, double expected)
        => Assert.Equal(expected, TableLayoutGeometry.CropXToPageX(cropX, zoneX, zoneW), 6);

    [Fact]
    public void ToPageX_maps_a_separator_list_preserving_order_and_scale()
    {
        // a zone occupying the middle 50% of the page; crop separators at 1/3 and 2/3 of the crop.
        var page = TableLayoutGeometry.ToPageX(new[] { 1.0 / 3, 2.0 / 3 }, zoneX: 0.25, zoneW: 0.50);
        Assert.Equal(2, page.Count);
        Assert.Equal(0.25 + (1.0 / 3) * 0.50, page[0], 6);
        Assert.Equal(0.25 + (2.0 / 3) * 0.50, page[1], 6);
        Assert.True(page[1] > page[0]);
    }

    [Fact]
    public void Detect_then_transform_round_trips_into_a_sub_zone()
    {
        // End-to-end of the pure path: detect in the crop frame, then project into a zone the user drew
        // as the right half of the page. Every page separator must therefore be > 0.5.
        var crop = TableLayoutGeometry.DetectColumnBoundaries(MichelinPage1());
        var page = TableLayoutGeometry.ToPageX(crop, zoneX: 0.5, zoneW: 0.5);
        Assert.Equal(crop.Count, page.Count);
        foreach (var x in page) Assert.InRange(x, 0.5, 1.0);
    }
}
