using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Imaging;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace OcrPipeline.Web.Services.Zonal;

/// <summary>
/// Template-based (zonal) extraction: for a ZONAL template, OCR ONLY inside each field's drawn zone
/// and feed the result straight into the field — no reliance on whatever blocks full-page OCR would
/// produce. The page is deskewed once, then each zone is cropped, upscaled, and read with a tight
/// PageSegMode + optional whitelist (<see cref="ZoneHint"/>). Normalization / transformers / review
/// flags all run through <see cref="MappingEngine.RunZonalAsync"/> — the same path as OCR-first.
///
/// TABLE_CELL fields (line items, Phase 2): the table zone is OCR'd once for word boxes (geometry),
/// rows are found by <see cref="TableRowSegmenter"/> (anchor-column-validated), then each cell is read
/// individually with its column's hint and collapsed via <see cref="CellLineSelector"/>.
/// </summary>
public sealed class ZonalExtractionService(
    IRegionOcrEngine regionOcr,
    ImagePreprocessor preprocessor,
    PagePreviewRenderer previewRenderer,
    MappingEngine mappingEngine,
    TextNormalizer normalizer,
    IOptions<TesseractOptions> tessOptions,
    IOptions<LineItemConsolidationOptions> consolidationOptions,
    ILogger<ZonalExtractionService> logger)
{
    private readonly TesseractOptions _o = tessOptions.Value;
    private readonly LineItemConsolidationOptions _consolidation = consolidationOptions.Value;

    /// <summary>One segmented + typed table field result: line_item rows and an aggregate confidence.</summary>
    public readonly record struct TableResult(List<Dictionary<string, object?>> Rows, decimal? Conf);

    /// <summary>
    /// Pure-ish core: build the outcome from delegate seams so tests can supply canned results (no
    /// images, no Tesseract, no DB). Scalar zones come from <paramref name="ocrZone"/>; TABLE_CELL
    /// fields (when <paramref name="ocrTable"/> + <paramref name="columnsByField"/> are supplied) come
    /// from <paramref name="ocrTable"/>.
    /// </summary>
    public async Task<MappingOutcome> BuildAsync(
        MappingTemplate template,
        Func<MappingField, Task<(string Raw, decimal Conf)>> ocrZone,
        IReadOnlyDictionary<int, List<TransformerStep>> steps,
        Func<MappingField, IReadOnlyList<MappingTableColumn>, Task<TableResult>>? ocrTable = null,
        IReadOnlyDictionary<int, List<MappingTableColumn>>? columnsByField = null,
        CancellationToken ct = default)
    {
        var results = new Dictionary<int, (string Raw, decimal Conf)>();
        Dictionary<int, (List<Dictionary<string, object?>> Rows, decimal? Conf)>? tableResults = null;

        foreach (var field in template.Fields)
        {
            if (field.ZoneX is null) continue; // field has no zone
            bool isTable = string.Equals(field.SourceType, "TABLE_CELL", StringComparison.OrdinalIgnoreCase);

            if (isTable)
            {
                if (ocrTable is null || columnsByField is null) continue;
                if (!columnsByField.TryGetValue(field.FieldId, out var cols)) continue;
                var active = cols.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToList();
                if (active.Count == 0) continue;

                ct.ThrowIfCancellationRequested();
                var tr = await ocrTable(field, active);
                (tableResults ??= new())[field.FieldId] = (tr.Rows, tr.Conf);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                results[field.FieldId] = await ocrZone(field);
            }
        }

        return await mappingEngine.RunZonalAsync(template, results, steps, tableResults, ct);
    }

    /// <summary>
    /// Pure-ish MULTI-PAGE core (Phase 3) with the same delegate seams as <see cref="BuildAsync"/> so
    /// it is offline-testable. Page roles are by POSITION (<see cref="PageRoleResolver"/>): scalar
    /// header/total zones are read ONCE on the page their role owns (FIRST=p1, LAST=last page); each
    /// physical page's table region (one per page, role-resolved with fallback) is OCR'd and its rows
    /// CONCATENATED into one line_item list (<see cref="MultiPageTable.Concat"/>) emitted under the
    /// group's canonical field id. A 1-page doc resolves to FIRST+LAST on the one page.
    /// </summary>
    public async Task<MappingOutcome> BuildMultiPageAsync(
        MappingTemplate template,
        int totalPages,
        Func<MappingField, int, Task<(string Raw, decimal Conf)>> ocrScalarOnPage,
        Func<MappingField, IReadOnlyList<MappingTableColumn>, int, Task<TableResult>> ocrTableOnPage,
        IReadOnlyDictionary<int, List<TransformerStep>> steps,
        IReadOnlyDictionary<int, List<MappingTableColumn>>? columnsByField = null,
        CancellationToken ct = default)
    {
        totalPages = Math.Max(1, totalPages);
        var results = new Dictionary<int, (string Raw, decimal Conf)>();
        var tableResults = new Dictionary<int, (List<Dictionary<string, object?>> Rows, decimal? Conf)>();

        // Scalar header/total zones: read once, on the first page their role occupies.
        foreach (var field in template.Fields)
        {
            if (field.ZoneX is null) continue;
            if (string.Equals(field.SourceType, "TABLE_CELL", StringComparison.OrdinalIgnoreCase)) continue;

            int? page = FirstPageForRole(PageRoleResolver.Parse(field.ZonePageRole), totalPages);
            if (page is null) continue;   // role never occurs in this doc (e.g. CONTINUATION when N<3)
            ct.ThrowIfCancellationRequested();
            results[field.FieldId] = await ocrScalarOnPage(field, page.Value);
        }

        // Table regions grouped by TargetProperty (the role-tagged FIRST/CONTINUATION/LAST regions of
        // one line_item table). One region per physical page; rows concatenated in page order.
        var tableFields = template.Fields
            .Where(f => f.ZoneX is not null && string.Equals(f.SourceType, "TABLE_CELL", StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<MappingTableColumn> ActiveCols(int fieldId) =>
            columnsByField is not null && columnsByField.TryGetValue(fieldId, out var raw)
                ? raw.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToList()
                : new List<MappingTableColumn>();

        foreach (var group in tableFields.GroupBy(f => f.TargetProperty, StringComparer.OrdinalIgnoreCase))
        {
            var byRole = new Dictionary<PageRole, MappingField>();
            foreach (var f in group) byRole[PageRoleResolver.Parse(f.ZonePageRole)] = f;   // designer prevents dup roles
            var available = byRole.Keys.ToHashSet();

            // canonical emitter AND the authoritative column STRUCTURE: the FIRST region if drawn,
            // else the lowest field id in the group.
            var canonical = byRole.TryGetValue(PageRole.First, out var firstRegion)
                ? firstRegion : group.OrderBy(f => f.FieldId).First();
            var canonicalCols = ActiveCols(canonical.FieldId);

            var perPage = new List<(int Page, List<Dictionary<string, object?>> Rows, decimal? Conf)>();
            for (int p = 1; p <= totalPages; p++)
            {
                var role = PageRoleResolver.PickTableRole(available, p, totalPages);
                if (role is null) continue;
                var region = byRole[role.Value];
                var regionCols = ActiveCols(region.FieldId);
                if (regionCols.Count == 0) continue;

                // Impose the canonical (FIRST) column STRUCTURE on this region's x-GEOMETRY so a drifted
                // sibling can't change the output's keys/types; on a column-count mismatch fall back to
                // the region's own columns and surface it.
                var cols = MultiPageColumns.Resolve(canonicalCols, regionCols, out var mismatch);
                if (mismatch)
                    logger.LogWarning(
                        "Multi-page table '{Property}': region role {Role} has {RegionCount} columns vs canonical {CanonicalCount} on page {Page}; using the region's own columns.",
                        canonical.TargetProperty, role.Value, regionCols.Count, canonicalCols.Count, p);

                ct.ThrowIfCancellationRequested();
                var tr = await ocrTableOnPage(region, cols, p);
                perPage.Add((p, tr.Rows, tr.Conf));
            }

            var merged = MultiPageTable.Concat(perPage);
            tableResults[canonical.FieldId] = (merged.Rows, merged.Conf);
        }

        return await mappingEngine.RunZonalAsync(
            template, results, steps, tableResults.Count > 0 ? tableResults : null, ct);
    }

    /// <summary>The page a role-tagged SCALAR field is read on (once): FIRST=1, LAST=last page,
    /// CONTINUATION=first middle page (null when the doc has no middle page).</summary>
    private static int? FirstPageForRole(PageRole role, int totalPages) => role switch
    {
        PageRole.First => 1,
        PageRole.Last => totalPages,
        PageRole.Continuation => totalPages >= 3 ? 2 : (int?)null,
        _ => 1
    };

    /// <summary>
    /// Segment a table zone's word boxes into rows (anchor-validated) and read each cell via
    /// <paramref name="readCell"/>, collapsing multi-line cells per column and typing them through the
    /// shared normalizer. Pure of image/OCR types (the cell read is a delegate) so it is unit-testable.
    /// </summary>
    public async Task<TableResult> BuildTableRowsAsync(
        MappingField tableField,
        IReadOnlyList<MappingTableColumn> columns,
        IReadOnlyList<WordBox> zoneWords,
        int[]? inkProfile,
        Func<int, MappingTableColumn, RowBand, Task<(string Raw, decimal Conf)>> readCell,
        ConsolidateOptions? consolidate = null,
        CancellationToken ct = default)
    {
        double zoneX = (double)(tableField.ZoneX ?? 0m), zoneW = (double)(tableField.ZoneW ?? 0m);
        var anchor = columns.FirstOrDefault(c => c.IsAnchor) ?? columns[0];
        double axs = TableGeometry.ToZoneRelativeX((double)(anchor.ColXStart ?? (decimal)zoneX), zoneX, zoneW);
        double axe = TableGeometry.ToZoneRelativeX((double)(anchor.ColXEnd ?? (decimal)(zoneX + zoneW)), zoneX, zoneW);

        var rawBands = TableRowSegmenter.Segment(zoneWords, axs, axe, inkProfile: inkProfile);

        // Consolidate (Option A): drop the footer / qty-bearing subtotal row so it is not a phantom item.
        // GATED — the drop/clean lexicon is layout-specific (Michelin), so it runs only when the caller
        // opts the template in (consolidate != null); otherwise every band is kept unchanged.
        IReadOnlyList<RowBand> bands = rawBands;
        if (consolidate is not null)
        {
            var selection = LineItemConsolidator.SelectItemRows(rawBands, zoneWords, axs, axe, consolidate);
            bands = selection.Kept;
            foreach (var d in selection.Dropped)
                logger.LogInformation(
                    "LineItem consolidator dropped a footer/subtotal row in '{Property}' (qty={Qty}, footerKeyword={Kw}, qtyEqualsRunningSum={Sum}).",
                    tableField.TargetProperty, d.Qty, d.FooterKeyword, d.QtyEqualsRunningSum);
        }

        var order = normalizer.InferDayMonthOrder(zoneWords.Select(w => w.Text));

        // zone-relative x-range for a column (its ColX* are page-normalized like the zone rect).
        (double S, double E) Range(MappingTableColumn c) => (
            TableGeometry.ToZoneRelativeX((double)(c.ColXStart ?? (decimal)zoneX), zoneX, zoneW),
            TableGeometry.ToZoneRelativeX((double)(c.ColXEnd ?? (decimal)(zoneX + zoneW)), zoneX, zoneW));

        var rows = new List<Dictionary<string, object?>>(bands.Count);
        var confs = new List<decimal>();
        for (int r = 0; r < bands.Count; r++)
        {
            var obj = new Dictionary<string, object?>();
            foreach (var col in columns)
            {
                ct.ThrowIfCancellationRequested();
                var (raw, conf) = await readCell(r, col, bands[r]);
                IReadOnlyList<string> cellLines = (raw ?? "").Replace("\r", "").Split('\n');
                // (b) strip shipping/reference metadata absorbed into a text (description) cell — gated.
                if (consolidate is not null && IsTextColumn(col))
                    cellLines = LineItemConsolidator.CleanDescriptionLines(cellLines, consolidate);
                var collapsed = CellLineSelector.Apply(
                    cellLines, col.LineSelectMode, col.LineSelectIndices, col.LineJoinSeparator);

                // Fallback: a tight per-cell crop can miss a thin glyph (a lone "1") or an edge row; the
                // whole-zone pass already has every word, so assemble the cell from the word boxes that
                // fall inside (this row band ∩ this column).
                if (collapsed.Length == 0)
                {
                    var (s, e) = Range(col);
                    var cellWords = zoneWords
                        .Where(w => w.XCenter >= s && w.XCenter <= e && w.YCenter >= bands[r].YStart && w.YCenter <= bands[r].YEnd)
                        .ToList();
                    if (cellWords.Count > 0)
                    {
                        IReadOnlyList<string> fbLines = GroupWordsToLines(cellWords);
                        if (consolidate is not null && IsTextColumn(col))
                            fbLines = LineItemConsolidator.CleanDescriptionLines(fbLines, consolidate);
                        collapsed = CellLineSelector.Apply(
                            fbLines, col.LineSelectMode, col.LineSelectIndices, col.LineJoinSeparator);
                        conf = Math.Round(cellWords.Average(w => w.Conf), 4);
                    }
                }

                obj[col.TargetSubProperty] = mappingEngine.NormalizeTyped(col.DataType, collapsed, order);
                if (collapsed.Length > 0) confs.Add(conf);
            }
            rows.Add(obj);
        }

        decimal? conf2 = confs.Count > 0 ? Math.Round(confs.Average(), 4) : null;
        return new TableResult(rows, conf2);
    }

    /// <summary>Production path: render a working raster per page, deskew once, crop + OCR each zone.</summary>
    public async Task<MappingOutcome> ProcessAsync(
        Document doc,
        MappingTemplate template,
        IReadOnlyDictionary<int, List<TransformerStep>> steps,
        IReadOnlyDictionary<int, List<MappingTableColumn>>? columnsByField = null,
        CancellationToken ct = default)
    {
        var preparedPages = new Dictionary<int, Image<L8>>();
        var tempPageFiles = new List<string>();
        // Layout-specific line-item consolidation runs only for gated-in templates (see LineItemConsolidationOptions).
        var consolidate = _consolidation.AppliesTo(template.TemplateId) ? new ConsolidateOptions() : null;
        try
        {
            return await BuildAsync(template,
                ocrZone: async field =>
                {
                    int pageNo = field.ZonePage ?? 1;
                    var page = GetPreparedPage(doc, pageNo, preparedPages, tempPageFiles);
                    var rect = ZoneGeometry.ToPixelRect(
                        field.ZoneX!.Value, field.ZoneY ?? 0m, field.ZoneW ?? 0m, field.ZoneH ?? 0m,
                        page.Width, page.Height);
                    var (psm, whitelist) = ZoneHint.Resolve(field.ZoneOcrHint, field.ZonePsm);
                    return await CropAndReadAsync(page, rect, psm, whitelist, doc.OcrLanguages, ct);
                },
                steps,
                ocrTable: async (field, cols) =>
                {
                    int pageNo = field.ZonePage ?? 1;
                    var page = GetPreparedPage(doc, pageNo, preparedPages, tempPageFiles);
                    var zoneRect = ZoneGeometry.ToPixelRect(
                        field.ZoneX!.Value, field.ZoneY ?? 0m, field.ZoneW ?? 0m, field.ZoneH ?? 0m,
                        page.Width, page.Height);
                    double zoneX = (double)(field.ZoneX ?? 0m), zoneW = (double)(field.ZoneW ?? 0m);

                    // (1) OCR the whole zone once for word boxes (geometry) + build the ink profile.
                    var (words, profile) = await ReadZoneWordsAndProfileAsync(page, zoneRect, doc.OcrLanguages, ct);
                    var boxes = words.Select(w => new WordBox(w.Text, w.X, w.Y, w.W, w.H, w.Conf)).ToList();

                    // (2) segment rows + read each cell individually with its column's hint.
                    return await BuildTableRowsAsync(field, cols, boxes, profile,
                        readCell: async (_, col, band) =>
                        {
                            double relS = TableGeometry.ToZoneRelativeX((double)(col.ColXStart ?? (decimal)zoneX), zoneX, zoneW);
                            double relE = TableGeometry.ToZoneRelativeX((double)(col.ColXEnd ?? (decimal)(zoneX + zoneW)), zoneX, zoneW);
                            var cellRect = TableGeometry.CellPixelRect(zoneRect, relS, relE, band);
                            var (cpsm, cwl) = ColumnOcr(col);
                            return await CropAndReadAsync(page, cellRect, cpsm, cwl, doc.OcrLanguages, ct);
                        }, consolidate: consolidate, ct: ct);
                },
                columnsByField,
                ct);
        }
        finally
        {
            foreach (var p in preparedPages.Values) p.Dispose();
            foreach (var f in tempPageFiles) if (File.Exists(f)) File.Delete(f);
        }
    }

    /// <summary>Production MULTI-PAGE path (Phase 3): same per-zone / per-cell OCR primitives as
    /// <see cref="ProcessAsync"/>, but the page each zone is read on is driven by page-role position
    /// (<see cref="BuildMultiPageAsync"/>), with line_item rows concatenated across pages.</summary>
    public async Task<MappingOutcome> ProcessMultiPageAsync(
        Document doc,
        MappingTemplate template,
        IReadOnlyDictionary<int, List<TransformerStep>> steps,
        IReadOnlyDictionary<int, List<MappingTableColumn>>? columnsByField = null,
        CancellationToken ct = default)
    {
        var preparedPages = new Dictionary<int, Image<L8>>();
        var tempPageFiles = new List<string>();
        // Layout-specific line-item consolidation runs only for gated-in templates (see LineItemConsolidationOptions).
        var consolidate = _consolidation.AppliesTo(template.TemplateId) ? new ConsolidateOptions() : null;
        try
        {
            return await BuildMultiPageAsync(template, Math.Max(1, doc.PageCount),
                ocrScalarOnPage: async (field, pageNo) =>
                {
                    var page = GetPreparedPage(doc, pageNo, preparedPages, tempPageFiles);
                    var rect = ZoneGeometry.ToPixelRect(
                        field.ZoneX!.Value, field.ZoneY ?? 0m, field.ZoneW ?? 0m, field.ZoneH ?? 0m,
                        page.Width, page.Height);
                    var (psm, whitelist) = ZoneHint.Resolve(field.ZoneOcrHint, field.ZonePsm);
                    return await CropAndReadAsync(page, rect, psm, whitelist, doc.OcrLanguages, ct);
                },
                ocrTableOnPage: async (field, cols, pageNo) =>
                {
                    var page = GetPreparedPage(doc, pageNo, preparedPages, tempPageFiles);
                    var zoneRect = ZoneGeometry.ToPixelRect(
                        field.ZoneX!.Value, field.ZoneY ?? 0m, field.ZoneW ?? 0m, field.ZoneH ?? 0m,
                        page.Width, page.Height);
                    double zoneX = (double)(field.ZoneX ?? 0m), zoneW = (double)(field.ZoneW ?? 0m);

                    var (words, profile) = await ReadZoneWordsAndProfileAsync(page, zoneRect, doc.OcrLanguages, ct);
                    var boxes = words.Select(w => new WordBox(w.Text, w.X, w.Y, w.W, w.H, w.Conf)).ToList();

                    return await BuildTableRowsAsync(field, cols, boxes, profile,
                        readCell: async (_, col, band) =>
                        {
                            double relS = TableGeometry.ToZoneRelativeX((double)(col.ColXStart ?? (decimal)zoneX), zoneX, zoneW);
                            double relE = TableGeometry.ToZoneRelativeX((double)(col.ColXEnd ?? (decimal)(zoneX + zoneW)), zoneX, zoneW);
                            var cellRect = TableGeometry.CellPixelRect(zoneRect, relS, relE, band);
                            var (cpsm, cwl) = ColumnOcr(col);
                            return await CropAndReadAsync(page, cellRect, cpsm, cwl, doc.OcrLanguages, ct);
                        }, consolidate: consolidate, ct: ct);
                },
                steps, columnsByField, ct);
        }
        finally
        {
            foreach (var p in preparedPages.Values) p.Dispose();
            foreach (var f in tempPageFiles) if (File.Exists(f)) File.Delete(f);
        }
    }

    /// <summary>A free-text (description) column — the only kind the metadata line-cleaner runs on, so a
    /// numeric/date column is never altered. Null/blank DataType is treated as text (matches ColumnOcr).</summary>
    private static bool IsTextColumn(MappingTableColumn col)
        => (col.DataType ?? "STRING").Trim().ToUpperInvariant() is "STRING" or "TEXT";

    /// <summary>OCR settings for a table cell from its column DataType: numeric/date columns get a tight
    /// single-line PSM + whitelist (reuses <see cref="ZoneHint"/>); text gets a block PSM so a wrapped
    /// description reads as multiple lines (later collapsed by <see cref="CellLineSelector"/>).</summary>
    private static (int Psm, string? Whitelist) ColumnOcr(MappingTableColumn col)
    {
        string hint = (col.DataType ?? "STRING").Trim().ToUpperInvariant() switch
        {
            "DECIMAL" => "NUMERIC",
            "INT" => "INT",
            "DATE" => "DATE",
            _ => "TEXT"
        };
        return hint == "TEXT" ? (6, null) : ZoneHint.Resolve(hint, null);
    }

    /// <summary>Crop a region at NATIVE resolution and read it with a tight PSM. Crop upscaling is now
    /// engine-owned (Tesseract enlarges toward MinOcrWidth; PaddleOCR wants native), so we hand over the
    /// unscaled crop and let the region engine apply the preprocessing it actually wants.</summary>
    private async Task<(string Raw, decimal Conf)> CropAndReadAsync(
        Image<L8> page, PixelRect rect, int psm, string? whitelist, string? languages, CancellationToken ct)
    {
        string crop = Path.Combine(Path.GetTempPath(), $"docuflow_zone_{Guid.NewGuid():N}.png");
        try
        {
            using (var img = page.Clone(c => c.Crop(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height))))
            {
                img.SaveAsPng(crop);
            }
            return await regionOcr.OcrRegionAsync(crop, psm, whitelist, languages, ct);
        }
        finally
        {
            if (File.Exists(crop)) File.Delete(crop);
        }
    }

    /// <summary>OCR a whole table zone for word boxes (block PSM) at NATIVE resolution and compute its
    /// horizontal ink profile from the same native crop. The profile is consumed in NORMALIZED y by
    /// <see cref="TableRowSegmenter"/>, so its pixel resolution does not affect row boundaries; crop
    /// upscaling for OCR is engine-owned (Tesseract enlarges; PaddleOCR wants native).</summary>
    private async Task<(IReadOnlyList<RegionWord> Words, int[] Profile)> ReadZoneWordsAndProfileAsync(
        Image<L8> page, PixelRect rect, string? languages, CancellationToken ct)
    {
        string crop = Path.Combine(Path.GetTempPath(), $"docuflow_ztab_{Guid.NewGuid():N}.png");
        int[] profile;
        try
        {
            using (var img = page.Clone(c => c.Crop(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height))))
            {
                img.SaveAsPng(crop);
                var px = new L8[img.Width * img.Height];
                img.CopyPixelDataTo(px);
                var buf = new byte[px.Length];
                for (int i = 0; i < px.Length; i++) buf[i] = px[i].PackedValue;
                profile = ImagePreprocessor.HorizontalInkProfile(buf, img.Width, img.Height);
            }
            var words = await regionOcr.OcrRegionWordsAsync(crop, psm: 6, whitelist: null, languages, ct);
            return (words, profile);
        }
        finally
        {
            if (File.Exists(crop)) File.Delete(crop);
        }
    }

    /// <summary>Group word boxes into reading-order text lines (a new line starts when a word clears the
    /// current line's bottom); each line's words are joined left-to-right by a space. Cell-read fallback.</summary>
    private static List<string> GroupWordsToLines(List<WordBox> words)
    {
        var lines = new List<string>();
        var current = new List<WordBox>();
        double bottom = double.NegativeInfinity;
        foreach (var w in words.OrderBy(w => w.YCenter))
        {
            if (current.Count > 0 && w.Y > bottom) { lines.Add(JoinLine(current)); current.Clear(); }
            current.Add(w);
            bottom = current.Count == 1 ? w.YBottom : Math.Max(bottom, w.YBottom);
        }
        if (current.Count > 0) lines.Add(JoinLine(current));
        return lines;
    }

    private static string JoinLine(List<WordBox> line) => string.Join(' ', line.OrderBy(w => w.X).Select(w => w.Text));

    private Image<L8> GetPreparedPage(Document doc, int pageNo, Dictionary<int, Image<L8>> cache, List<string> temps)
    {
        if (cache.TryGetValue(pageNo, out var cached)) return cached;

        var (path, isTemp) = previewRenderer.RenderForCrop(doc.StoredPath, doc.ContentType, pageNo, _o.Dpi);
        if (isTemp) temps.Add(path);
        var prepared = preprocessor.PreparePage(path); // grayscale + deskew once
        cache[pageNo] = prepared;
        return prepared;
    }
}
