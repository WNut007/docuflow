namespace OcrPipeline.Web.Services.Zonal;

/// <summary>Tunables for <see cref="LineItemConsolidator"/>. Defaults carry the Michelin/B.F. Goodrich
/// shipping-invoice lexicon; every list is overridable so other layouts can supply their own vocabulary.</summary>
public sealed record ConsolidateOptions
{
    /// <summary>A description line is DROPPED when it (trimmed) STARTS WITH one of these (case-insensitive).
    /// These are per-item shipping/reference annotations, never the goods description.</summary>
    public IReadOnlyList<string> MetadataPrefixes { get; init; } =
    [
        "AVIEXP", "Warehouse", "JC Number", "UC Number", "Container Number", "Seal Number",
        "Our Reference", "Your Order", "Origin :", "Origin:",
    ];

    /// <summary>A description line is DROPPED when it CONTAINS one of these (case-insensitive) — product
    /// boilerplate repeated on every row that adds nothing over the size/spec line.</summary>
    public IReadOnlyList<string> MetadataPhrases { get; init; } =
    [
        "Passenger Car Radial Tyre", "Light Truck Tyre", "Michelin Brand", "B.F. Goodrich",
    ];

    /// <summary>An anchor (qty-bearing) row is DROPPED as a footer/subtotal when its text CONTAINS one of
    /// these (case-insensitive). This is the PRIMARY drop signal — it must fire for a row to be removed;
    /// a qty-equals-running-sum match alone never drops a row (it can only corroborate, see remarks).</summary>
    public IReadOnlyList<string> FooterKeywords { get; init; } =
    [
        "FCA FACTORY", "PLACE OF DELIVERY", "CIP ", "Freight", "Insurance", "Inland",
    ];
}

/// <summary>
/// Row classifier / consolidator that sits BETWEEN <see cref="TableRowSegmenter"/> and the per-cell read
/// (the segmenter already does coalescing job (a): a qty band absorbs the following anchor-less lines as
/// its description up to the next qty band). PURE and free of image/OCR/DB types — like the segmenter —
/// so it is unit-testable over synthetic <see cref="WordBox"/>/<see cref="RowBand"/> data.
///
/// Two jobs, matching the Option-A spec:
///   (c)/(d) <see cref="SelectItemRows"/> — drop the footer block / qty-bearing subtotal row (e.g.
///           "270 PC FCA FACTORY ... CIP PLACE OF DELIVERY") so it never becomes a phantom 15th item.
///           Qty-LESS totals (Freight/Insurance/Inland on their own) never form an anchor band at all,
///           so they need no special handling. The subtotal is the HARD case because it CARRIES a qty
///           and so looks like a real item; it is dropped on a FOOTER KEYWORD hit (primary). Whether its
///           qty equals the running sum of the kept items is returned as a CONFIRMATORY signal only —
///           never a trigger on its own, so it can't eat a legitimate last item that happens to repeat a
///           qty (e.g. items 1+2 = 3 and a final "Labor qty 3").
///   (b) <see cref="CleanDescriptionLines"/> — drop shipping/reference annotation lines (AVIEXP, Container
///       Number, Our Reference, Your Order, Origin, and product boilerplate) absorbed into a kept item's
///       description, leaving the goods/size spec line(s).
/// </summary>
public static class LineItemConsolidator
{
    /// <summary>Outcome of <see cref="SelectItemRows"/>: the kept item bands (footer/subtotal removed),
    /// plus diagnostics for the dropped rows so the caller can log WHY a row was removed.</summary>
    public readonly record struct RowSelection(
        IReadOnlyList<RowBand> Kept,
        IReadOnlyList<DroppedRow> Dropped);

    /// <summary>A row the consolidator removed: its band, the qty it carried (null if unparsable), and the
    /// signals — <paramref name="FooterKeyword"/> is the primary reason; <paramref name="QtyEqualsRunningSum"/>
    /// only corroborates (the qty matched the sum of the items kept above it).</summary>
    public readonly record struct DroppedRow(RowBand Band, long? Qty, bool FooterKeyword, bool QtyEqualsRunningSum);

    /// <summary>
    /// (c)/(d) Filter the segmenter's anchor bands to the real line items, dropping the footer/subtotal.
    /// </summary>
    /// <param name="bands">Bands from <see cref="TableRowSegmenter.Segment"/>, in top-to-bottom order.</param>
    /// <param name="zoneWords">All zone words (coords normalized 0..1 to the zone), to read each band's text.</param>
    /// <param name="anchorXStart">Anchor column left edge, normalized 0..1 relative to the zone.</param>
    /// <param name="anchorXEnd">Anchor column right edge, normalized 0..1 relative to the zone.</param>
    public static RowSelection SelectItemRows(
        IReadOnlyList<RowBand> bands,
        IReadOnlyList<WordBox> zoneWords,
        double anchorXStart,
        double anchorXEnd,
        ConsolidateOptions? options = null)
    {
        options ??= new ConsolidateOptions();
        if (bands is null || bands.Count == 0) return new RowSelection(Array.Empty<RowBand>(), Array.Empty<DroppedRow>());

        var kept = new List<RowBand>(bands.Count);
        var dropped = new List<DroppedRow>();
        long runningSum = 0;   // sum of the qtys of the items KEPT so far

        foreach (var band in bands)
        {
            var inBand = (zoneWords ?? Array.Empty<WordBox>())
                .Where(w => w.YCenter >= band.YStart && w.YCenter <= band.YEnd)
                .ToList();

            string bandText = string.Join(" ", inBand.OrderBy(w => w.YCenter).ThenBy(w => w.X).Select(w => w.Text));
            long? qty = ParseQty(inBand, anchorXStart, anchorXEnd);

            bool footer = ContainsAny(bandText, options.FooterKeywords);

            if (footer)
            {
                // Drop. qtyEqualsSum is recorded purely as corroboration; it is NEVER the trigger.
                bool corroborates = qty is { } q && q == runningSum;
                dropped.Add(new DroppedRow(band, qty, FooterKeyword: true, QtyEqualsRunningSum: corroborates));
                continue;
            }

            kept.Add(band);
            if (qty is { } qk) runningSum += qk;
        }

        return new RowSelection(kept, dropped);
    }

    /// <summary>
    /// (b) Drop shipping/reference annotation + boilerplate lines from one description cell's lines,
    /// preserving order. Lines are trimmed; blank lines are dropped. Returns the surviving goods/spec
    /// lines (possibly empty if the cell was entirely metadata).
    /// </summary>
    public static IReadOnlyList<string> CleanDescriptionLines(IEnumerable<string> lines, ConsolidateOptions? options = null)
    {
        options ??= new ConsolidateOptions();
        var kept = new List<string>();
        foreach (var raw in lines ?? Array.Empty<string>())
        {
            string line = (raw ?? string.Empty).Trim();
            if (line.Length == 0) continue;
            if (IsMetadataLine(line, options)) continue;
            kept.Add(line);
        }
        return kept;
    }

    /// <summary>True when a (trimmed) description line is shipping/reference metadata to drop.</summary>
    private static bool IsMetadataLine(string line, ConsolidateOptions o)
    {
        foreach (var p in o.MetadataPrefixes)
            if (line.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var ph in o.MetadataPhrases)
            if (line.Contains(ph, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> needles)
    {
        foreach (var n in needles)
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The integer qty in the anchor column for a band: the anchor-column words joined in reading
    /// order (covering a value split across tokens) and parsed; null when nothing parses.</summary>
    private static long? ParseQty(IReadOnlyList<WordBox> bandWords, double anchorXStart, double anchorXEnd)
    {
        var anchorTokens = bandWords
            .Where(w => w.XCenter >= anchorXStart && w.XCenter <= anchorXEnd)
            .OrderBy(w => w.X)
            .Select(w => new string(w.Text.Where(char.IsDigit).ToArray()))
            .Where(s => s.Length > 0)
            .ToList();
        if (anchorTokens.Count == 0) return null;
        return long.TryParse(string.Concat(anchorTokens), out var v) ? v : null;
    }
}
