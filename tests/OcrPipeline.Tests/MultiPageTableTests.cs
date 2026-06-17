using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Pure, offline coverage of cross-page row concatenation (Phase 3): page order, empty-page
/// skipping, and confidence aggregation.</summary>
public sealed class MultiPageTableTests
{
    private static Dictionary<string, object?> Row(string desc, long qty)
        => new() { ["description"] = desc, ["qty"] = qty };

    [Fact]
    public void Concatenates_rows_in_ascending_page_order()
    {
        // deliberately out of order to prove the sort
        var merged = MultiPageTable.Concat(new[]
        {
            (3, new List<Dictionary<string, object?>> { Row("p3a", 5) }, (decimal?)0.90m),
            (1, new List<Dictionary<string, object?>> { Row("p1a", 1), Row("p1b", 2) }, (decimal?)0.80m),
            (2, new List<Dictionary<string, object?>> { Row("p2a", 3) }, (decimal?)0.70m),
        });

        Assert.Equal(4, merged.Rows.Count);
        Assert.Equal(new[] { "p1a", "p1b", "p2a", "p3a" },
            merged.Rows.Select(r => (string)r["description"]!).ToArray());
        Assert.Equal(0.80m, merged.Conf);   // (0.80 + 0.70 + 0.90) / 3
    }

    [Fact]
    public void Skips_empty_pages_and_ignores_their_confidence()
    {
        var merged = MultiPageTable.Concat(new[]
        {
            (1, new List<Dictionary<string, object?>> { Row("a", 1) }, (decimal?)0.60m),
            (2, new List<Dictionary<string, object?>>(), (decimal?)0.10m),   // empty -> skipped, conf ignored
            (3, new List<Dictionary<string, object?>> { Row("b", 2) }, (decimal?)0.80m),
        });

        Assert.Equal(new[] { "a", "b" }, merged.Rows.Select(r => (string)r["description"]!).ToArray());
        Assert.Equal(0.70m, merged.Conf);   // (0.60 + 0.80) / 2, the empty page excluded
    }

    [Fact]
    public void No_rows_yields_empty_and_null_confidence()
    {
        var merged = MultiPageTable.Concat(Array.Empty<(int, List<Dictionary<string, object?>>, decimal?)>());
        Assert.Empty(merged.Rows);
        Assert.Null(merged.Conf);
    }

    [Fact]
    public void Tags_each_row_with_its_source_page()
    {
        var merged = MultiPageTable.Concat(new[]
        {
            (1, new List<Dictionary<string, object?>> { Row("a", 1), Row("b", 2) }, (decimal?)0.9m),
            (2, new List<Dictionary<string, object?>> { Row("c", 3) }, (decimal?)0.9m),
        });
        Assert.Equal(new[] { 1, 1, 2 },
            merged.Rows.Select(r => System.Convert.ToInt32(r[OcrPipeline.Web.Services.Mapping.LineItemTable.ReservedPageKey])).ToArray());
    }
}
