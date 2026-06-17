using OcrPipeline.Web.Services.Normalization;

namespace OcrPipeline.Web.Services.Mapping;

/// <summary>
/// PURE helpers for the multi-page line-item review table (Phase 3): grouping concatenated rows by
/// their source page (for the page-jump strip) and flagging junk rows whose numeric anchor cell has
/// no digit. No I/O — unit-testable, and the anchor check mirrors the client-side rule in review.js.
/// </summary>
public static class ReviewTableHelpers
{
    /// <summary>One contiguous run of rows from the same source page.</summary>
    public sealed record PageGroup(int Page, int FirstRowIndex, int Count);

    /// <summary>Group consecutive rows by their page marker (rows arrive in page order, so each page is
    /// one contiguous run). Drives the compact "P1·5 P2·5 P3·2" jump strip.</summary>
    public static List<PageGroup> GroupByPage(IReadOnlyList<int> rowPages)
    {
        var groups = new List<PageGroup>();
        for (int i = 0; i < rowPages.Count;)
        {
            int page = rowPages[i], start = i;
            while (i < rowPages.Count && rowPages[i] == page) i++;
            groups.Add(new PageGroup(page, start, i - start));
        }
        return groups;
    }

    /// <summary>A row is junk when its ANCHOR cell should be numeric (INT/DECIMAL) but contains no
    /// digit after Thai-digit normalization — e.g. an OCR-hallucinated "ม" row from a too-tall zone.
    /// Non-numeric anchor types are always valid. Mirrors the check in review.js.</summary>
    public static bool AnchorValueValid(string? anchorDataType, string? value)
    {
        var dt = (anchorDataType ?? "STRING").Trim().ToUpperInvariant();
        if (dt != "INT" && dt != "DECIMAL") return true;          // only numeric anchors are junk-checkable
        if (string.IsNullOrWhiteSpace(value)) return false;       // a numeric anchor needs a value
        return TextNormalizer.NormalizeThaiDigits(value).Any(c => c is >= '0' and <= '9');
    }
}
