using OcrPipeline.Web.Services.Mapping;

namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// PURE merge of per-page table results into one ordered line_item list (Phase 3). Each page's rows
/// are produced independently by the unchanged Phase-2 segmentation (<c>BuildTableRowsAsync</c>); this
/// concatenates them in ascending page order, skips pages that yielded no rows, and averages the
/// non-empty pages' confidences. No I/O — unit-testable on plain dictionaries.
/// </summary>
public static class MultiPageTable
{
    public static (List<Dictionary<string, object?>> Rows, decimal? Conf) Concat(
        IEnumerable<(int Page, List<Dictionary<string, object?>> Rows, decimal? Conf)> pages)
    {
        var rows = new List<Dictionary<string, object?>>();
        var confs = new List<decimal>();
        foreach (var p in pages.OrderBy(p => p.Page))
        {
            if (p.Rows is null || p.Rows.Count == 0) continue;   // a page may legitimately have no rows
            // Tag each row with its source page (review UI only — stripped before export).
            foreach (var row in p.Rows) row[LineItemTable.ReservedPageKey] = p.Page;
            rows.AddRange(p.Rows);
            if (p.Conf is { } c) confs.Add(c);
        }
        decimal? conf = confs.Count > 0 ? Math.Round(confs.Average(), 4) : null;
        return (rows, conf);
    }
}
