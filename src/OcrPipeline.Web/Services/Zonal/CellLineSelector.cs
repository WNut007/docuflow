namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// Collapses a multi-line table cell into ONE value per the column's rule (set once in the template,
/// applied to every document). Pure and testable.
///   ALL   - join every non-empty line with the separator (default a single space)
///   FIRST - the first non-empty line only
///   PICK  - the lines at <c>indices</c> (0-based, e.g. "0,2"), joined with the separator
/// Unknown/blank mode defaults to ALL. This is the LineSelectMode/LineSelectIndices/LineJoinSeparator
/// triple stored on <see cref="Domain.MappingTableColumn"/>.
/// </summary>
public static class CellLineSelector
{
    public static string Apply(IReadOnlyList<string> lines, string? mode, string? indices, string? separator)
    {
        var clean = (lines ?? Array.Empty<string>())
            .Select(l => (l ?? "").Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (clean.Count == 0) return "";

        string sep = separator ?? " ";

        switch ((mode ?? "ALL").Trim().ToUpperInvariant())
        {
            case "FIRST":
                return clean[0];

            case "PICK":
                var picked = ParseIndices(indices)
                    .Where(i => i >= 0 && i < clean.Count)
                    .Select(i => clean[i])
                    .ToList();
                return string.Join(sep, picked.Count > 0 ? picked : clean);

            default: // ALL
                return string.Join(sep, clean);
        }
    }

    private static IEnumerable<int> ParseIndices(string? indices)
        => (indices ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var i) ? i : -1);
}
