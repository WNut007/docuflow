using System.Text.Json;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Mapping;

/// <summary>
/// Round-trips a line_item TABLE_CELL value between its stored typed JSON array and a flat table of
/// strings (one string per cell) for the review screen. Parsing keeps each number's on-disk text, so
/// decimal scale (e.g. "100.00") survives a view round-trip; rebuilding re-types every cell through
/// the SAME path as extraction (a <paramref name="typeCell"/> delegate wired to
/// <see cref="MappingEngine.NormalizeTyped"/>), so a reviewer's edit yields the same JSON shape the
/// pipeline emits — qty stays an int, prices stay decimals. Pure of DB/OCR types, so unit-testable.
/// </summary>
public static class LineItemTable
{
    /// <summary>Reserved per-row key carrying the source page (Phase 3 multi-page). Display/highlight
    /// only — ignored by cell parsing, preserved across the round-trip, and stripped before export.</summary>
    public const string ReservedPageKey = "_pg";

    /// <summary>Parse the stored typed JSON array into display-string rows keyed by sub-property.
    /// Numbers use their raw JSON text (preserves scale); strings use their value; missing/null -> "".</summary>
    public static List<Dictionary<string, string>> Parse(string? json, IReadOnlyList<MappingTableColumn> cols)
    {
        var rows = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(json)) return rows;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return rows;

        foreach (var rowEl in doc.RootElement.EnumerateArray())
        {
            var row = new Dictionary<string, string>();
            foreach (var col in cols)
            {
                string val = "";
                if (rowEl.ValueKind == JsonValueKind.Object &&
                    rowEl.TryGetProperty(col.TargetSubProperty, out var cell))
                {
                    val = cell.ValueKind switch
                    {
                        JsonValueKind.Number => cell.GetRawText(),          // preserve scale: 100.00
                        JsonValueKind.String => cell.GetString() ?? "",
                        JsonValueKind.True or JsonValueKind.False => cell.GetRawText(),
                        JsonValueKind.Null => "",
                        _ => cell.GetRawText()
                    };
                }
                row[col.TargetSubProperty] = val;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Read the reserved per-row page marker (<see cref="ReservedPageKey"/>) for each row, in
    /// order; a missing/invalid marker -> 1 (single-page/legacy). Parallel to <see cref="Parse"/>.</summary>
    public static List<int> ReadPageTags(string? json)
    {
        var pages = new List<int>();
        if (string.IsNullOrWhiteSpace(json)) return pages;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return pages;

        foreach (var rowEl in doc.RootElement.EnumerateArray())
        {
            int pg = 1;
            if (rowEl.ValueKind == JsonValueKind.Object
                && rowEl.TryGetProperty(ReservedPageKey, out var pgEl)
                && pgEl.ValueKind == JsonValueKind.Number && pgEl.TryGetInt32(out var v) && v >= 1)
                pg = v;
            pages.Add(pg);
        }
        return pages;
    }

    /// <summary>Rebuild typed rows from edited display strings, typing each cell per its column's
    /// DataType via <paramref name="typeCell"/> (wire to <see cref="MappingEngine.NormalizeTyped"/>).
    /// The caller serializes the result with System.Text.Json to get the stored JSON.</summary>
    public static List<Dictionary<string, object?>> BuildTypedRows(
        IReadOnlyList<MappingTableColumn> cols,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        Func<string, string?, object?> typeCell)
    {
        var result = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var obj = new Dictionary<string, object?>();
            foreach (var col in cols)
            {
                row.TryGetValue(col.TargetSubProperty, out var raw);
                obj[col.TargetSubProperty] = typeCell(col.DataType, raw);
            }
            // preserve page provenance through the round-trip (survives edit/delete/reorder)
            if (row.TryGetValue(ReservedPageKey, out var pg) && int.TryParse(pg, out var pgi) && pgi >= 1)
                obj[ReservedPageKey] = pgi;
            result.Add(obj);
        }
        return result;
    }
}
