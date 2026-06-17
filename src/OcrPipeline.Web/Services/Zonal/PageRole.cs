namespace OcrPipeline.Web.Services.Zonal;

/// <summary>Position-based role of a physical page in a multi-page document (Phase 3).</summary>
public enum PageRole { First, Continuation, Last }

/// <summary>
/// PURE page-role resolution for multi-page zonal extraction. No I/O. Roles are by POSITION:
/// page 1 = FIRST (header + table), middle pages = CONTINUATION (table only), last page = LAST
/// (table + totals). A single-page document is FIRST <b>and</b> LAST on the one page.
///
/// Two distinct questions:
///   - <see cref="AppliesTo"/> — does a role-tagged SCALAR field (header/totals) read on this page?
///     Read-once membership: FIRST on page 1, LAST on the last page (so on a 1-page doc both fire).
///   - <see cref="PrimaryRole"/> / <see cref="PickTableRole"/> — which SINGLE table region feeds this
///     page (exactly one, so rows are never double-read), with a LAST→CONTINUATION→FIRST fallback.
/// </summary>
public static class PageRoleResolver
{
    /// <summary>Parse the stored ZonePageRole string. Unknown/null (legacy "ANY") -> false.</summary>
    public static bool TryParse(string? role, out PageRole parsed)
    {
        switch (role?.Trim().ToUpperInvariant())
        {
            case "FIRST": parsed = PageRole.First; return true;
            case "CONTINUATION" or "CONT": parsed = PageRole.Continuation; return true;
            case "LAST": parsed = PageRole.Last; return true;
            default: parsed = PageRole.First; return false;
        }
    }

    /// <summary>Parse with a FIRST default for an unrecognised/null role.</summary>
    public static PageRole Parse(string? role) => TryParse(role, out var r) ? r : PageRole.First;

    /// <summary>The canonical string stored in MappingField.ZonePageRole.</summary>
    public static string ToToken(PageRole role) => role switch
    {
        PageRole.Continuation => "CONTINUATION",
        PageRole.Last => "LAST",
        _ => "FIRST"
    };

    /// <summary>Does a scalar field with <paramref name="role"/> read on page <paramref name="pageNo"/>
    /// of <paramref name="totalPages"/>? (1-based.) On a single page FIRST and LAST both apply.</summary>
    public static bool AppliesTo(PageRole role, int pageNo, int totalPages) => role switch
    {
        PageRole.First => pageNo == 1,
        PageRole.Last => pageNo == totalPages,
        PageRole.Continuation => pageNo > 1 && pageNo < totalPages,
        _ => false
    };

    /// <summary>The single role that owns a page's table region: FIRST for page 1, LAST for the last
    /// page, CONTINUATION for the middle. (1-page -> FIRST; 2-page -> FIRST, LAST; no CONTINUATION.)</summary>
    public static PageRole PrimaryRole(int pageNo, int totalPages)
        => pageNo <= 1 ? PageRole.First
         : pageNo >= totalPages ? PageRole.Last
         : PageRole.Continuation;

    /// <summary>Pick the table-region role for a page from the regions actually drawn
    /// (<paramref name="available"/>): the primary role, else fall back LAST→CONTINUATION→FIRST so a
    /// missing LAST/CONTINUATION region reuses an earlier one. Null when no region is available.</summary>
    public static PageRole? PickTableRole(IReadOnlySet<PageRole> available, int pageNo, int totalPages)
    {
        foreach (var role in FallbackChain(PrimaryRole(pageNo, totalPages)))
            if (available.Contains(role)) return role;
        // A single page is FIRST and LAST: never silently drop its table — accept any region that
        // exists (e.g. the only region was tagged LAST). Multi-page pages keep the strict backward
        // chain above, so an earlier page can never borrow a later page's region.
        if (pageNo == 1 && pageNo >= totalPages)
            foreach (var role in new[] { PageRole.First, PageRole.Last, PageRole.Continuation })
                if (available.Contains(role)) return role;
        return null;
    }

    private static PageRole[] FallbackChain(PageRole primary) => primary switch
    {
        PageRole.Last => new[] { PageRole.Last, PageRole.Continuation, PageRole.First },
        PageRole.Continuation => new[] { PageRole.Continuation, PageRole.First },
        _ => new[] { PageRole.First }
    };
}
