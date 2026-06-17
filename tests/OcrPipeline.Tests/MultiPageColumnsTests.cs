using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Pure coverage of extraction-authoritative columns (Phase 3): the canonical (FIRST) region
/// owns column STRUCTURE; each region keeps its own x-GEOMETRY; a column-count mismatch falls back to
/// the region's own columns and flags it.</summary>
public sealed class MultiPageColumnsTests
{
    private static MappingTableColumn C(string sub, string dt, bool anchor, decimal xs, decimal xe, int sort)
        => new() { TargetSubProperty = sub, DataType = dt, IsAnchor = anchor, ColXStart = xs, ColXEnd = xe, SortOrder = sort, IsActive = true };

    [Fact]
    public void Imposes_canonical_structure_and_keeps_region_geometry()
    {
        var canonical = new List<MappingTableColumn> { C("qty", "INT", true, 0.00m, 0.20m, 0), C("amount", "DECIMAL", false, 0.20m, 1.00m, 1) };
        // sibling drifted: wrong sub-names / types / anchor + its own (different) x-geometry
        var region = new List<MappingTableColumn> { C("QTY_DRIFT", "STRING", false, 0.05m, 0.25m, 0), C("amt_drift", "STRING", true, 0.25m, 0.95m, 1) };

        var resolved = MultiPageColumns.Resolve(canonical, region, out var mismatch);

        Assert.False(mismatch);
        Assert.Equal(new[] { "qty", "amount" }, resolved.Select(c => c.TargetSubProperty).ToArray());   // structure
        Assert.Equal(new[] { "INT", "DECIMAL" }, resolved.Select(c => c.DataType).ToArray());
        Assert.True(resolved[0].IsAnchor); Assert.False(resolved[1].IsAnchor);
        Assert.Equal(0.05m, resolved[0].ColXStart); Assert.Equal(0.25m, resolved[0].ColXEnd);            // geometry
        Assert.Equal(0.25m, resolved[1].ColXStart); Assert.Equal(0.95m, resolved[1].ColXEnd);
    }

    [Fact]
    public void Count_mismatch_falls_back_to_region_columns_and_flags()
    {
        var canonical = new List<MappingTableColumn> { C("qty", "INT", true, 0m, 0.5m, 0), C("amount", "DECIMAL", false, 0.5m, 1m, 1) };
        var region = new List<MappingTableColumn> { C("only", "STRING", true, 0m, 1m, 0) };

        var resolved = MultiPageColumns.Resolve(canonical, region, out var mismatch);

        Assert.True(mismatch);
        Assert.Equal("only", Assert.Single(resolved).TargetSubProperty);   // region's own columns used
    }

    [Fact]
    public void Empty_canonical_uses_region_columns()
    {
        var region = new List<MappingTableColumn> { C("x", "STRING", true, 0m, 1m, 0) };
        var resolved = MultiPageColumns.Resolve(new List<MappingTableColumn>(), region, out var mismatch);
        Assert.False(mismatch);
        Assert.Equal("x", Assert.Single(resolved).TargetSubProperty);
    }
}
