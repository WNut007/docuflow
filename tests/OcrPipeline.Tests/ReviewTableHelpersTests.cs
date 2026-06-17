using OcrPipeline.Web.Services.Mapping;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Pure coverage of the multi-page review helpers: page grouping for the jump strip and the
/// numeric-anchor junk-row flag.</summary>
public sealed class ReviewTableHelpersTests
{
    [Fact]
    public void GroupByPage_runs_consecutive_pages()
    {
        var groups = ReviewTableHelpers.GroupByPage(new[] { 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3 });
        Assert.Equal(3, groups.Count);
        Assert.Equal((1, 0, 5), (groups[0].Page, groups[0].FirstRowIndex, groups[0].Count));
        Assert.Equal((2, 5, 5), (groups[1].Page, groups[1].FirstRowIndex, groups[1].Count));
        Assert.Equal((3, 10, 2), (groups[2].Page, groups[2].FirstRowIndex, groups[2].Count));
    }

    [Fact]
    public void GroupByPage_handles_single_page_and_empty()
    {
        Assert.Equal(new[] { (1, 0, 3) },
            ReviewTableHelpers.GroupByPage(new[] { 1, 1, 1 }).Select(g => (g.Page, g.FirstRowIndex, g.Count)).ToArray());
        Assert.Empty(ReviewTableHelpers.GroupByPage(System.Array.Empty<int>()));
    }

    [Theory]
    [InlineData("INT", "1", true)]
    [InlineData("INT", "๑", true)]        // Thai digit normalizes to a digit
    [InlineData("INT", "ม", false)]       // OCR junk -> no digit -> flagged
    [InlineData("INT", "", false)]
    [InlineData("DECIMAL", "100.00", true)]
    [InlineData("DECIMAL", "—", false)]
    [InlineData("STRING", "ม", true)]     // non-numeric anchors are never junk
    [InlineData("DATE", "anything", true)]
    public void AnchorValueValid_flags_only_numeric_anchors_without_a_digit(string dt, string value, bool valid)
        => Assert.Equal(valid, ReviewTableHelpers.AnchorValueValid(dt, value));
}
