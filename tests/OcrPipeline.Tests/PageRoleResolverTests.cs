using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Pure, offline coverage of position-based page-role resolution (Phase 3). Locks the FIRST /
/// CONTINUATION / LAST behaviour the orchestrator relies on for N = 1..4 pages, including the
/// single-page FIRST+LAST case that must not break the validated single-page flow.
/// </summary>
public sealed class PageRoleResolverTests
{
    // ---- scalar read-once membership (AppliesTo) ------------------------------

    [Fact]
    public void Single_page_is_first_and_last_but_not_continuation()
    {
        Assert.True(PageRoleResolver.AppliesTo(PageRole.First, 1, 1));
        Assert.True(PageRoleResolver.AppliesTo(PageRole.Last, 1, 1));     // header + totals on the one page
        Assert.False(PageRoleResolver.AppliesTo(PageRole.Continuation, 1, 1));
    }

    [Fact]
    public void Two_pages_split_first_then_last_no_continuation()
    {
        Assert.True(PageRoleResolver.AppliesTo(PageRole.First, 1, 2));
        Assert.False(PageRoleResolver.AppliesTo(PageRole.Last, 1, 2));
        Assert.True(PageRoleResolver.AppliesTo(PageRole.Last, 2, 2));
        Assert.False(PageRoleResolver.AppliesTo(PageRole.First, 2, 2));
        Assert.False(PageRoleResolver.AppliesTo(PageRole.Continuation, 1, 2));
        Assert.False(PageRoleResolver.AppliesTo(PageRole.Continuation, 2, 2));
    }

    [Theory]
    [InlineData(1, true, false, false)]   // FIRST
    [InlineData(2, false, true, false)]   // CONTINUATION
    [InlineData(3, false, false, true)]   // LAST
    public void Three_pages_first_continuation_last(int page, bool first, bool cont, bool last)
    {
        Assert.Equal(first, PageRoleResolver.AppliesTo(PageRole.First, page, 3));
        Assert.Equal(cont, PageRoleResolver.AppliesTo(PageRole.Continuation, page, 3));
        Assert.Equal(last, PageRoleResolver.AppliesTo(PageRole.Last, page, 3));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Four_pages_have_two_continuation_pages(int middlePage)
        => Assert.True(PageRoleResolver.AppliesTo(PageRole.Continuation, middlePage, 4));

    // ---- table-region primary role (one per page) ----------------------------

    [Theory]
    [InlineData(1, 1, PageRole.First)]
    [InlineData(1, 2, PageRole.First)]
    [InlineData(2, 2, PageRole.Last)]
    [InlineData(1, 3, PageRole.First)]
    [InlineData(2, 3, PageRole.Continuation)]
    [InlineData(3, 3, PageRole.Last)]
    public void PrimaryRole_picks_one_role_per_page(int page, int total, PageRole expected)
        => Assert.Equal(expected, PageRoleResolver.PrimaryRole(page, total));

    [Fact]
    public void PickTableRole_uses_primary_when_available()
    {
        var all = new HashSet<PageRole> { PageRole.First, PageRole.Continuation, PageRole.Last };
        Assert.Equal(PageRole.First, PageRoleResolver.PickTableRole(all, 1, 3));
        Assert.Equal(PageRole.Continuation, PageRoleResolver.PickTableRole(all, 2, 3));
        Assert.Equal(PageRole.Last, PageRoleResolver.PickTableRole(all, 3, 3));
    }

    [Fact]
    public void PickTableRole_falls_back_when_region_missing()
    {
        // only FIRST + CONTINUATION drawn: a 3-page doc's LAST page reuses CONTINUATION
        var noLast = new HashSet<PageRole> { PageRole.First, PageRole.Continuation };
        Assert.Equal(PageRole.Continuation, PageRoleResolver.PickTableRole(noLast, 3, 3));

        // only FIRST drawn: every page reuses FIRST
        var onlyFirst = new HashSet<PageRole> { PageRole.First };
        Assert.Equal(PageRole.First, PageRoleResolver.PickTableRole(onlyFirst, 2, 3));
        Assert.Equal(PageRole.First, PageRoleResolver.PickTableRole(onlyFirst, 3, 3));

        Assert.Null(PageRoleResolver.PickTableRole(new HashSet<PageRole>(), 1, 1));
    }

    [Fact]
    public void Single_page_accepts_any_region_so_a_lone_last_table_is_not_dropped()
    {
        Assert.Equal(PageRole.Last, PageRoleResolver.PickTableRole(new HashSet<PageRole> { PageRole.Last }, 1, 1));
        Assert.Equal(PageRole.Continuation, PageRoleResolver.PickTableRole(new HashSet<PageRole> { PageRole.Continuation }, 1, 1));
        // but a multi-page FIRST page must NOT borrow a LAST region (strict backward chain)
        Assert.Null(PageRoleResolver.PickTableRole(new HashSet<PageRole> { PageRole.Last }, 1, 3));
    }

    // ---- parsing the stored token --------------------------------------------

    [Theory]
    [InlineData("FIRST", PageRole.First)]
    [InlineData("continuation", PageRole.Continuation)]
    [InlineData("CONT", PageRole.Continuation)]
    [InlineData("Last", PageRole.Last)]
    public void TryParse_reads_known_tokens(string token, PageRole expected)
    {
        Assert.True(PageRoleResolver.TryParse(token, out var role));
        Assert.Equal(expected, role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ANY")]
    [InlineData("")]
    public void TryParse_rejects_null_or_legacy_any(string? token)
        => Assert.False(PageRoleResolver.TryParse(token, out _));
}
