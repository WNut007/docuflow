using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Pure row-segmentation tests over synthetic word boxes (no images/OCR) — the riskiest Phase-2 logic.
/// Coords are normalized 0..1 relative to the table zone. Anchor column (QTY) lives at x in [0.30,0.45];
/// description column at x in [0,0.30].
/// </summary>
public sealed class TableRowSegmenterTests
{
    private const double AnchorXs = 0.30, AnchorXe = 0.45;

    private static WordBox Desc(double y, double h = 0.04) => new("desc", 0.05, y, 0.20, h, 0.9m);
    private static WordBox Qty(double y, double h = 0.04) => new("1", 0.35, y, 0.03, h, 0.9m);

    private static bool Contains(RowBand b, double y) => y >= b.YStart && y <= b.YEnd;

    [Fact]
    public void Three_single_line_rows_yield_three_bands()
    {
        var words = new[]
        {
            Desc(0.10), Qty(0.10),
            Desc(0.30), Qty(0.30),
            Desc(0.50), Qty(0.50),
        };
        var rows = TableRowSegmenter.Segment(words, AnchorXs, AnchorXe);

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, b => Contains(b, 0.12));   // each anchor center lands in exactly one band
        Assert.Contains(rows, b => Contains(b, 0.32));
        Assert.Contains(rows, b => Contains(b, 0.52));
        // bands tile the zone top-to-bottom without gaps
        Assert.Equal(0.0, rows[0].YStart, 3);
        Assert.Equal(1.0, rows[^1].YEnd, 3);
    }

    [Fact]
    public void Multi_line_description_stays_in_one_row()
    {
        // row 1 description wraps to a second line (no anchor value on that line)
        var words = new[]
        {
            Desc(0.10), Qty(0.10),
            Desc(0.155),                 // continuation line — no anchor
            Desc(0.30), Qty(0.30),
            Desc(0.50), Qty(0.50),
        };
        var rows = TableRowSegmenter.Segment(words, AnchorXs, AnchorXe);

        Assert.Equal(3, rows.Count);                          // not 4 — continuation merged
        var row1 = rows[0];
        Assert.True(Contains(row1, 0.12), "anchor line in row 1");
        Assert.True(Contains(row1, 0.175), "wrapped description line in row 1");
    }

    [Fact]
    public void Close_rows_are_split_by_the_anchor()
    {
        // two rows whose baselines are very close: a projection-only splitter would merge them,
        // but each has its own anchor value, so the anchor forces two rows.
        var words = new[]
        {
            Desc(0.100), Qty(0.100),
            Desc(0.150), Qty(0.150),
        };
        var rows = TableRowSegmenter.Segment(words, AnchorXs, AnchorXe);

        Assert.Equal(2, rows.Count);
        Assert.True(Contains(rows[0], 0.120) && !Contains(rows[1], 0.120));
        Assert.True(Contains(rows[1], 0.170) && !Contains(rows[0], 0.170));
    }

    [Fact]
    public void Lines_before_the_first_anchor_attach_to_row_one()
    {
        // stray text above the first anchor (e.g. a wrapped header line inside the zone) -> row 1
        var words = new[]
        {
            Desc(0.04),                  // before any anchor
            Desc(0.10), Qty(0.10),
            Desc(0.30), Qty(0.30),
        };
        var rows = TableRowSegmenter.Segment(words, AnchorXs, AnchorXe);

        Assert.Equal(2, rows.Count);
        Assert.Equal(0.0, rows[0].YStart, 3);
        Assert.True(Contains(rows[0], 0.06), "pre-anchor line falls in row 1");
    }

    [Fact]
    public void Split_anchor_tokens_on_one_baseline_merge_into_one_row()
    {
        // an anchor value OCR'd as two overlapping tokens on the same baseline = still one row
        var words = new[]
        {
            Desc(0.10), new WordBox("1", 0.35, 0.10, 0.02, 0.04, 0.9m), new WordBox("0", 0.375, 0.105, 0.02, 0.04, 0.9m),
            Desc(0.30), Qty(0.30),
        };
        var rows = TableRowSegmenter.Segment(words, AnchorXs, AnchorXe);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void No_anchor_words_yields_no_rows()
    {
        var words = new[] { Desc(0.10), Desc(0.30) };
        var rows = TableRowSegmenter.Segment(words, AnchorXs, AnchorXe);
        Assert.Empty(rows);
    }

    [Fact]
    public void Ink_profile_snaps_boundary_into_the_gap()
    {
        // two rows; profile has a clean zero-ink gap centered at index 20 of 40 (y ~= 0.5)
        var words = new[] { Desc(0.20), Qty(0.20), Desc(0.70), Qty(0.70) };
        var profile = new int[41];
        for (int i = 0; i < profile.Length; i++) profile[i] = (i is >= 18 and <= 22) ? 0 : 5;
        var rows = TableRowSegmenter.Segment(words, AnchorXs, AnchorXe, inkProfile: profile);

        Assert.Equal(2, rows.Count);
        Assert.Equal(0.5, rows[0].YEnd, 2);   // boundary snapped to the gap center, not the raw midpoint
    }
}
