using OcrPipeline.Tests.Fixtures;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Tests for the producer's PURE pixel→page mapping (CropCellsToPageBoundaries) against REAL captured
/// PP-Structure crop output (Fixtures/MichelinCropCapture.cs). Covers the normalize (pixel/crop-size),
/// the crop→page projection through the drawn zone, and — the #2 re-validation — that the full-page-tuned
/// support thresholds still recover columns on a SHORT line-item crop. The HTTP/image path isn't exercised
/// here; it's thin glue around this core and the graceful-degradation contract.
/// </summary>
public sealed class PaddleStructureTableDetectorTests
{
    private static double[][] Cells(double[][] px) => px;   // readability alias

    // -------- long crop (page 1): 7 columns resolved -> 6 separators --------

    [Fact]
    public void LongCrop_resolves_seven_columns_when_zone_is_the_whole_page()
    {
        // zone = whole page, so crop-frame == page-frame; boundaries come back as page-normalized directly.
        var b = PaddleStructureTableDetector.CropCellsToPageBoundaries(
            MichelinCropCapture.Page1CellsPx, MichelinCropCapture.Page1Width, MichelinCropCapture.Page1Height,
            new RectN(0, 0, 1, 1));

        Assert.Equal(6, b.Count);                       // 7 columns -> 6 interior separators
        for (int i = 1; i < b.Count; i++) Assert.True(b[i] > b[i - 1]);
        Assert.All(b, x => Assert.InRange(x, 0.0, 1.0));
    }

    // -------- short crop (page 3): the threshold re-validation --------

    [Fact]
    public void ShortCrop_recovers_six_columns_with_the_default_floor()
    {
        // 42 cells / ~7 rows — the case where MinSupportFloor=4 is the ACTIVE threshold. It must recover the
        // 6 logical Michelin columns (5 separators); a lower floor would over-split (re-admit a description
        // sub-cluster). This locks the "full-page defaults transfer to short crops" finding.
        var b = PaddleStructureTableDetector.CropCellsToPageBoundaries(
            MichelinCropCapture.Page3CellsPx, MichelinCropCapture.Page3Width, MichelinCropCapture.Page3Height,
            new RectN(0, 0, 1, 1));

        Assert.Equal(5, b.Count);                       // 6 columns -> 5 separators, no over-split
    }

    // -------- crop → page projection through a drawn zone --------

    [Fact]
    public void Boundaries_are_projected_into_the_drawn_zone()
    {
        // Same cells, but the user drew the zone as the right half of the page. Every separator must land in
        // that half, and be the whole-page result scaled+offset by the zone (crop frame is the zone).
        var whole = PaddleStructureTableDetector.CropCellsToPageBoundaries(
            MichelinCropCapture.Page3CellsPx, MichelinCropCapture.Page3Width, MichelinCropCapture.Page3Height,
            new RectN(0, 0, 1, 1));
        var zone = new RectN(0.5, 0.2, 0.5, 0.3);
        var projected = PaddleStructureTableDetector.CropCellsToPageBoundaries(
            MichelinCropCapture.Page3CellsPx, MichelinCropCapture.Page3Width, MichelinCropCapture.Page3Height, zone);

        Assert.Equal(whole.Count, projected.Count);
        for (int i = 0; i < whole.Count; i++)
        {
            Assert.InRange(projected[i], 0.5, 1.0);
            Assert.Equal(zone.X + whole[i] * zone.W, projected[i], 6);   // offset + scale by the zone
        }
    }

    // -------- empty / malformed inputs --------

    [Fact]
    public void Empty_or_degenerate_input_yields_no_boundaries()
    {
        Assert.Empty(PaddleStructureTableDetector.CropCellsToPageBoundaries(
            Array.Empty<double[]>(), 1000, 1000, new RectN(0, 0, 1, 1)));
        // zero crop size
        Assert.Empty(PaddleStructureTableDetector.CropCellsToPageBoundaries(
            MichelinCropCapture.Page3CellsPx, 0, 0, new RectN(0, 0, 1, 1)));
    }

    [Fact]
    public void Malformed_cell_boxes_are_skipped_not_thrown()
    {
        var cells = new[]
        {
            new[] { 10.0, 5, 30, 12 },        // ok
            new[] { 10.0, 5, 30 },            // wrong arity -> skipped
            new double[0],                    // empty -> skipped
            new[] { 200.0, 5, 230, 12 },      // ok (a 2nd column)
        };
        // 2 valid cells across 2 x-bands but only 1 row each — below support, so no columns. Must not throw.
        var b = PaddleStructureTableDetector.CropCellsToPageBoundaries(cells, 300, 100, new RectN(0, 0, 1, 1));
        Assert.Empty(b);
    }
}
