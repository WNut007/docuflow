using OcrPipeline.Web.Services.Zonal;
using Xunit;
using TF = OcrPipeline.Web.Services.Zonal.ZonalSaveValidator.TableFieldInfo;

namespace OcrPipeline.Tests;

/// <summary>
/// Pure, offline coverage of the zone-designer save guard (Phase 3, option-a authoring UX). Locks the
/// two machine-checkable invariants the designer enforces by construction but the API must still
/// defend: within one table (grouped by TargetProperty) page-roles are distinct, and a multi-region
/// table gives every region a role. Divergent NAMES are prevented in the UI, not here (the group key
/// is the name), so they are intentionally not part of these tests.
/// </summary>
public sealed class ZonalSaveValidatorTests
{
    [Fact]
    public void Valid_first_continuation_last_sharing_one_name_is_accepted()
    {
        var r = ZonalSaveValidator.Validate(new[]
        {
            new TF("line_item", "FIRST"),
            new TF("line_item", "CONTINUATION"),
            new TF("line_item", "LAST"),
        });
        Assert.True(r.IsValid);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Duplicate_role_within_a_table_is_rejected()
    {
        // the field-7 / field-8 repro: two CONTINUATION regions of the same table
        var r = ZonalSaveValidator.Validate(new[]
        {
            new TF("line_item", "FIRST"),
            new TF("line_item", "CONTINUATION"),
            new TF("line_item", "CONTINUATION"),
            new TF("line_item", "LAST"),
        });
        Assert.False(r.IsValid);
        Assert.Contains("CONTINUATION", r.Error);
    }

    [Fact]
    public void Multi_region_table_with_a_roleless_region_is_rejected()
    {
        var r = ZonalSaveValidator.Validate(new[]
        {
            new TF("line_item", "FIRST"),
            new TF("line_item", null),       // a 2nd region with no role -> ambiguous
        });
        Assert.False(r.IsValid);
        Assert.Contains("page-role", r.Error);
    }

    [Fact]
    public void Single_region_single_page_table_with_null_role_is_accepted()
    {
        var r = ZonalSaveValidator.Validate(new[] { new TF("line_item", null) });
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Single_region_with_a_lone_role_is_accepted()
    {
        // one region tagged LAST only (legacy / single-region) is fine
        var r = ZonalSaveValidator.Validate(new[] { new TF("line_item", "LAST") });
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Two_separate_valid_tables_are_accepted()
    {
        var r = ZonalSaveValidator.Validate(new[]
        {
            new TF("line_item", "FIRST"),
            new TF("line_item", "LAST"),
            new TF("charges", "FIRST"),
            new TF("charges", "CONTINUATION"),
        });
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Grouping_is_case_and_whitespace_insensitive()
    {
        // " Line_Item " and "line_item" are the SAME table -> the two FIRST regions collide
        var r = ZonalSaveValidator.Validate(new[]
        {
            new TF(" Line_Item ", "FIRST"),
            new TF("line_item", "FIRST"),
        });
        Assert.False(r.IsValid);
        Assert.Contains("FIRST", r.Error);
    }

    [Fact]
    public void Cont_alias_normalizes_to_continuation_and_collides()
    {
        var r = ZonalSaveValidator.Validate(new[]
        {
            new TF("line_item", "CONTINUATION"),
            new TF("line_item", "CONT"),     // alias of CONTINUATION
        });
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Empty_input_is_accepted()
    {
        Assert.True(ZonalSaveValidator.Validate(System.Array.Empty<TF>()).IsValid);
    }
}
