namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// PURE geometry for Option ③-B "rough-box → auto-columns": given the table cells the OCR engine found
/// INSIDE a user-drawn zone (the crop), derive the interior COLUMN SEPARATORS and map them onto the
/// page. No I/O, no engine/HTTP types, no images — unit-testable against captured cell geometry.
///
/// We do NOT trust the engine's row/column grid order or its table region bbox (the capture showed
/// PP-Structure returns one page-spanning "table" whose grid mashes header/address/totals together).
/// Instead we cluster cell x-extents GEOMETRICALLY: real line-item rows repeat the same column edges
/// many times, so left edges (column starts) and right edges (column ends) form strong frequency
/// clusters even amid noise. This recovered the Michelin columns cleanly from the real capture.
///
/// Vertical extent is deliberately NOT detected — the zone rectangle is whatever the user drew
/// (PP-Structure is unreliable on a full page's vertical extent; ③-B sidesteps that by construction).
/// </summary>
public static class TableLayoutGeometry
{
    /// <summary>One cell's box, normalized 0..1 to the CROP (the user-drawn zone) the engine OCR'd.
    /// The producer divides the sidecar's pixel boxes by page_width/height to get these.</summary>
    public readonly record struct CellBox(double Left, double Top, double Right, double Bottom);

    /// <summary>Tunables for column detection (Q-D). Defaults derived from the Michelin capture; every
    /// layout can override. All x distances are fractions of the CROP width (0..1).</summary>
    public sealed record ColumnOptions
    {
        /// <summary>Edges within this fraction of crop width fold into one cluster (a column start/end).</summary>
        public double MergeTolerance { get; init; } = 0.02;

        /// <summary>An edge cluster is a real column only if at least this FRACTION of cells share it —
        /// line-item rows repeat their edges, sparse header/address cells don't.</summary>
        public double MinSupportFraction { get; init; } = 0.04;

        /// <summary>Absolute floor for support so small tables (few rows) still detect columns.</summary>
        public int MinSupportFloor { get; init; } = 4;
    }

    /// <summary>
    /// Detect interior column separators from the crop's cells, returned in the CROP frame (normalized
    /// 0..1, ascending). N columns → N-1 separators. Empty when fewer than two columns are found.
    /// </summary>
    public static IReadOnlyList<double> DetectColumnBoundaries(
        IReadOnlyList<CellBox> cells, ColumnOptions? options = null)
    {
        options ??= new ColumnOptions();
        if (cells is null || cells.Count == 0) return Array.Empty<double>();

        int minSupport = Math.Max(options.MinSupportFloor,
            (int)Math.Round(cells.Count * options.MinSupportFraction));

        // Column START anchors: strong clusters of cell LEFT edges, ascending.
        var starts = ClusterEdges(cells.Select(c => c.Left), options.MergeTolerance, minSupport);
        if (starts.Count < 2) return Array.Empty<double>();

        // Each column's RIGHT extent = the median right edge of the cells that belong to it (their left
        // edge is nearest this start, but not past the next start). Median resists a stray wide cell.
        var separators = new List<double>(starts.Count - 1);
        for (int i = 0; i < starts.Count - 1; i++)
        {
            double lo = starts[i], next = starts[i + 1];
            double mid = (lo + next) / 2.0;   // assign a cell to this column when its left edge is in [lo, next)
            var rights = cells
                .Where(c => c.Left >= lo - options.MergeTolerance && c.Left < next - options.MergeTolerance)
                .Select(c => c.Right)
                .Where(r => r > lo && r <= next)        // ignore cells spilling past the next column (spanning/garbage)
                .OrderBy(r => r)
                .ToList();

            // Separator sits in the whitespace between this column's right extent and the next start.
            // Fall back to the start-midpoint when a column has no clean right edge of its own.
            double rightExtent = rights.Count > 0 ? Median(rights) : mid;
            separators.Add((rightExtent + next) / 2.0);
        }
        return separators;
    }

    /// <summary>Map a crop-frame X (0..1 within the drawn zone) to a PAGE-normalized X. The crop spans
    /// exactly the zone, so cropX=0 → zone left edge and cropX=1 → zone right edge. No deskew term: the
    /// crop is a full-frame, non-deskewed sub-rectangle of the same backdrop, so the only transform is
    /// the zone's offset + scale.</summary>
    public static double CropXToPageX(double cropNormX, double zoneX, double zoneW)
        => zoneX + cropNormX * zoneW;

    /// <summary>Map a whole list of crop-frame separators to page-normalized X (preserves order).</summary>
    public static IReadOnlyList<double> ToPageX(IReadOnlyList<double> cropBoundaries, double zoneX, double zoneW)
        => (cropBoundaries ?? Array.Empty<double>()).Select(b => CropXToPageX(b, zoneX, zoneW)).ToList();

    // ---- helpers --------------------------------------------------------------

    /// <summary>Greedy 1-D clustering of edge positions: sort, start a new cluster when the gap to the
    /// previous edge exceeds <paramref name="tolerance"/>. Returns each kept cluster's MEAN, ascending,
    /// for clusters whose member count is at least <paramref name="minSupport"/>.</summary>
    private static List<double> ClusterEdges(IEnumerable<double> edges, double tolerance, int minSupport)
    {
        var sorted = edges.OrderBy(x => x).ToList();
        var reps = new List<double>();
        if (sorted.Count == 0) return reps;

        double sum = sorted[0];
        int count = 1;
        double prev = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - prev <= tolerance) { sum += sorted[i]; count++; }
            else { if (count >= minSupport) reps.Add(sum / count); sum = sorted[i]; count = 1; }
            prev = sorted[i];
        }
        if (count >= minSupport) reps.Add(sum / count);
        return reps;
    }

    private static double Median(IReadOnlyList<double> sortedAsc)
    {
        int n = sortedAsc.Count;
        return n % 2 == 1 ? sortedAsc[n / 2] : (sortedAsc[n / 2 - 1] + sortedAsc[n / 2]) / 2.0;
    }
}
