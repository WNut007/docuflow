namespace OcrPipeline.Web.Services.Zonal;

/// <summary>Tunables for <see cref="TableRowSegmenter"/>. Defaults suit typical invoice tables.</summary>
public sealed record RowSegmentOptions
{
    /// <summary>Two anchor-column words merge into ONE row only when their vertical overlap is at least
    /// this fraction of the smaller word's height — i.e. the same value split across tokens on one
    /// baseline. Distinct (even if close) baselines stay separate, so close rows are NOT merged.</summary>
    public double AnchorOverlapMerge { get; init; } = 0.5;

    /// <summary>An anchor candidate must be at least this fraction of the median word height to count as a
    /// value. Rejects hairline noise that lands in the anchor column — e.g. a vertical table border OCR'd
    /// as '|' (a tall-but-paper-thin, high-confidence speck) that would otherwise become a phantom row.</summary>
    public double MinAnchorHeightFactor { get; init; } = 0.4;
}

/// <summary>
/// CORE of zonal table extraction — the risky piece, kept PURE and free of image/OCR/DB types so it is
/// unit-testable over synthetic data (like <see cref="Ocr.ImagePreprocessor.EstimateSkewAngleDegrees"/>).
///
/// The ANCHOR column (the one the user marked as having exactly one value per row, e.g. QTY) is
/// AUTHORITATIVE for the row count: each anchor word is one row (words that vertically overlap merge,
/// covering a value split across tokens). Row y-bands then TILE the zone, split at the midpoint gap
/// between consecutive anchors — so a wrapped description (extra lines with no anchor value) falls
/// inside its row's band, content before the first anchor falls in row 1, and trailing lines fall in
/// the last row. Never a fixed grid; variable-height rows handled. Row count == anchor count.
///
/// An optional horizontal ink-projection profile (<see cref="Ocr.ImagePreprocessor.HorizontalInkProfile"/>)
/// snaps each inter-row boundary into the actual whitespace gap instead of a naive midpoint.
/// </summary>
public static class TableRowSegmenter
{
    /// <param name="words">Word boxes, coords normalized 0..1 relative to the table zone.</param>
    /// <param name="anchorXStart">Anchor column left edge, normalized 0..1 relative to the zone.</param>
    /// <param name="anchorXEnd">Anchor column right edge, normalized 0..1 relative to the zone.</param>
    /// <param name="options">Merge tunables (optional).</param>
    /// <param name="inkProfile">Optional horizontal ink profile (length = any vertical resolution of
    /// the zone) used to snap inter-row boundaries to the whitespace gap.</param>
    public static IReadOnlyList<RowBand> Segment(
        IReadOnlyList<WordBox> words,
        double anchorXStart,
        double anchorXEnd,
        RowSegmentOptions? options = null,
        int[]? inkProfile = null)
    {
        options ??= new RowSegmentOptions();
        if (words is null || words.Count == 0) return Array.Empty<RowBand>();

        // anchor words define the rows; non-anchor words are assigned to bands later (at cell read).
        // Reject specks (e.g. a '|' table border) shorter than a fraction of the median word height.
        double minAnchorH = options.MinAnchorHeightFactor * Median(words.Select(w => w.H));
        var anchors = words.Where(w => w.XCenter >= anchorXStart && w.XCenter <= anchorXEnd && w.H >= minAnchorH)
                           .OrderBy(w => w.YCenter)
                           .ToList();
        if (anchors.Count == 0) return Array.Empty<RowBand>();

        // merge anchor words that vertically overlap (a single value split across tokens) into clusters.
        var clusters = new List<(double Top, double Bottom)>();
        foreach (var a in anchors)
        {
            if (clusters.Count > 0 && OverlapRatio(clusters[^1], a) >= options.AnchorOverlapMerge)
            {
                var last = clusters[^1];
                clusters[^1] = (Math.Min(last.Top, a.Y), Math.Max(last.Bottom, a.YBottom));
            }
            else clusters.Add((a.Y, a.YBottom));
        }

        // bands tile [0,1], split at the gap between consecutive anchor clusters.
        int n = clusters.Count;
        var bands = new List<RowBand>(n);
        for (int i = 0; i < n; i++)
        {
            double yStart = i == 0 ? 0.0 : Boundary(clusters[i - 1].Bottom, clusters[i].Top, inkProfile);
            double yEnd = i == n - 1 ? 1.0 : Boundary(clusters[i].Bottom, clusters[i + 1].Top, inkProfile);
            bands.Add(new RowBand(Clamp01(yStart), Clamp01(Math.Max(yEnd, yStart))));
        }
        return bands;
    }

    private static double OverlapRatio((double Top, double Bottom) c, WordBox w)
    {
        double overlap = Math.Min(c.Bottom, w.YBottom) - Math.Max(c.Top, w.Y);
        if (overlap <= 0) return 0;
        double minH = Math.Min(c.Bottom - c.Top, w.H);
        return minH <= 0 ? 0 : overlap / minH;
    }

    /// <summary>Boundary between two rows: the center of the lowest-ink gap when a profile is given,
    /// else the midpoint between the rows' facing edges.</summary>
    private static double Boundary(double prevBottom, double nextTop, int[]? profile)
    {
        double lo = Math.Min(prevBottom, nextTop), hi = Math.Max(prevBottom, nextTop);
        double midpoint = (prevBottom + nextTop) / 2.0;
        if (profile is not { Length: > 1 } || hi <= lo) return midpoint;

        int len = profile.Length;
        int a = Clamp((int)Math.Floor(lo * (len - 1)), 0, len - 1);
        int b = Clamp((int)Math.Ceiling(hi * (len - 1)), 0, len - 1);
        if (b <= a) return midpoint;

        int min = int.MaxValue;
        for (int k = a; k <= b; k++) if (profile[k] < min) min = profile[k];

        // center of the longest run of minimum ink within [a,b]
        int bestStart = a, bestLen = 0, runStart = -1;
        for (int k = a; k <= b; k++)
        {
            if (profile[k] == min) { if (runStart < 0) runStart = k; int rl = k - runStart + 1; if (rl > bestLen) { bestLen = rl; bestStart = runStart; } }
            else runStart = -1;
        }
        return (bestStart + (bestLen - 1) / 2.0) / (len - 1);
    }

    private static double Median(IEnumerable<double> values)
    {
        var s = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (s.Count == 0) return 0;
        int mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
}
