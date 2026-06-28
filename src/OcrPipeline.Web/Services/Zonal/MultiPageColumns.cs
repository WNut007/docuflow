using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// PURE column resolution for a multi-page line_item table (Phase 3). The FIRST (canonical) region is
/// authoritative for column STRUCTURE — TargetSubProperty, DataType, IsAnchor, TableHeader, the
/// LineSelect* rules AND LineOffset — while each page's region keeps its own x-GEOMETRY (ColXStart/ColXEnd),
/// since
/// the table band legitimately differs per page. This makes the concatenated output immune to column
/// "drift" if a sibling region's structure was edited (or hand-edited in the DB) after a copy.
///
/// Guard: if a region's active-column COUNT differs from the canonical's, the structures can't be
/// paired safely, so the region's OWN columns are used and <paramref name="mismatch"/> is set true
/// (the caller surfaces it).
/// </summary>
public static class MultiPageColumns
{
    public static List<MappingTableColumn> Resolve(
        IReadOnlyList<MappingTableColumn> canonical,
        IReadOnlyList<MappingTableColumn> region,
        out bool mismatch)
    {
        mismatch = false;
        if (canonical.Count == 0) return region.ToList();                 // no canonical structure to impose
        if (region.Count != canonical.Count) { mismatch = true; return region.ToList(); }

        var merged = new List<MappingTableColumn>(region.Count);
        for (int i = 0; i < region.Count; i++)
        {
            var s = canonical[i];   // structure (authoritative)
            var g = region[i];      // geometry (this page's region)
            merged.Add(new MappingTableColumn
            {
                ColumnId = g.ColumnId, FieldId = g.FieldId, IsActive = true,
                TargetSubProperty = s.TargetSubProperty, DataType = s.DataType, IsAnchor = s.IsAnchor,
                TableHeader = s.TableHeader, SortOrder = s.SortOrder,
                LineSelectMode = s.LineSelectMode, LineSelectIndices = s.LineSelectIndices,
                LineJoinSeparator = s.LineJoinSeparator, LineOffset = s.LineOffset,   // canonical STRUCTURE — must be copied
                ColXStart = g.ColXStart, ColXEnd = g.ColXEnd,
            });
        }
        return merged;
    }
}
