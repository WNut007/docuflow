using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Transform;

namespace OcrPipeline.Web.Services.Mapping;

/// <summary>
/// Resolves a MappingTemplate against an OcrExtraction to produce the target model.
/// Each field's SourceType decides how its value is pulled from the OCR artifacts;
/// an optional transformer pipeline then preprocesses the value before mapping.
///
/// A TABLE_CELL field that has MappingTableColumn rows is emitted as an ARRAY of typed
/// objects (one per table row); without those rows it keeps single-value behavior.
/// </summary>
public sealed class MappingEngine(TransformerPipeline transformerPipeline, TextNormalizer normalizer)
{
    public async Task<MappingOutcome> RunAsync(
        MappingTemplate template,
        OcrExtraction extraction,
        IReadOnlyDictionary<int, List<TransformerStep>> stepsByField,
        IReadOnlyDictionary<int, List<MappingTableColumn>>? columnsByField = null,
        CancellationToken ct = default)
    {
        columnsByField ??= new Dictionary<int, List<MappingTableColumn>>();
        var dateOrder = normalizer.InferDayMonthOrder(extraction.TextBlocks.Select(b => b.Content));
        var outcome = new MappingOutcome
        {
            TemplateId = template.TemplateId,
            TargetModel = template.TargetModel
        };

        var model = new Dictionary<string, object?>();
        var confidences = new List<decimal>();

        // context shared by every transformer
        var allProps = BuildProperties(extraction);
        var fullText = string.Join('\n', extraction.TextBlocks.Select(b => b.Content));

        foreach (var field in template.Fields)
        {
            // Multi-column TABLE_CELL field -> emit an array of typed objects (one per row).
            if (string.Equals(field.SourceType, "TABLE_CELL", StringComparison.OrdinalIgnoreCase)
                && columnsByField.TryGetValue(field.FieldId, out var rawCols))
            {
                var cols = rawCols.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToList();
                if (cols.Count > 0)
                {
                    var built = BuildTableArray(field, extraction, cols, dateOrder);
                    var rows = built?.Rows ?? new List<Dictionary<string, object?>>();

                    var arrValue = new MappedValue
                    {
                        FieldId = field.FieldId,
                        TargetProperty = field.TargetProperty,
                        RawValue = null,
                        NormalizedValue = JsonSerializer.Serialize(rows),
                        Confidence = built?.Confidence,
                        SourceRef = built?.SourceRef,
                        IsBelowThreshold = built?.Confidence is { } ac && ac < field.MinConfidence
                    };
                    if (arrValue.Confidence is { } aconf) confidences.Add(aconf);

                    model[field.TargetProperty] = rows;   // real array -> typed JSON in MappedJson
                    outcome.Values.Add(arrValue);
                    continue;
                }
            }

            var resolved = Resolve(field, extraction);

            // run the transformer pipeline on the resolved value
            if (stepsByField.TryGetValue(field.FieldId, out var steps) && steps.Count > 0)
            {
                var ctx = new TransformContext(field.TargetProperty, allProps, fullText);
                resolved.NormalizedValue = await transformerPipeline.RunAsync(
                    resolved.NormalizedValue ?? resolved.RawValue, steps, ctx, ct);
            }

            resolved.FieldId = field.FieldId;
            resolved.TargetProperty = field.TargetProperty;
            resolved.IsBelowThreshold = resolved.Confidence is { } c && c < field.MinConfidence;

            if (resolved.Confidence is { } conf) confidences.Add(conf);

            model[field.TargetProperty] = resolved.NormalizedValue;
            outcome.Values.Add(resolved);
        }

        outcome.OverallConfidence = confidences.Count > 0 ? Math.Round(confidences.Average(), 4) : null;
        outcome.NeedsReview =
            outcome.Values.Any(v => v.IsBelowThreshold) ||
            template.Fields.Where(f => f.IsRequired)
                           .Any(f => string.IsNullOrWhiteSpace(
                               outcome.Values.First(v => v.FieldId == f.FieldId).NormalizedValue));

        outcome.MappedJson = JsonSerializer.Serialize(model,
            new JsonSerializerOptions { WriteIndented = true });
        return outcome;
    }

    private static Dictionary<string, string?> BuildProperties(OcrExtraction ex)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in ex.TextBlocks)
        {
            var idx = b.Content.IndexOf(':');
            if (idx <= 0) continue;
            dict[b.Content[..idx].Trim()] = b.Content[(idx + 1)..].Trim();
        }
        return dict;
    }

    private static MappedValue Resolve(MappingField field, OcrExtraction ex) => field.SourceType switch
    {
        "KEY_VALUE"  => FromKeyValue(field, ex),
        "REGEX"      => FromRegex(field, ex),
        "TABLE_CELL" => FromTable(field, ex),
        "CONSTANT"   => new MappedValue { RawValue = field.DefaultValue, NormalizedValue = field.DefaultValue, Confidence = 1m },
        _            => new MappedValue { Confidence = 0m }
    };

    private static MappedValue FromKeyValue(MappingField field, OcrExtraction ex)
    {
        if (field.KeyPattern is null) return Empty(field);
        var rx = new Regex(field.KeyPattern, RegexOptions.IgnoreCase);

        foreach (var b in ex.TextBlocks)
        {
            var idx = b.Content.IndexOf(':');
            if (idx <= 0) continue;
            var key = b.Content[..idx].Trim();
            var val = b.Content[(idx + 1)..].Trim();
            if (rx.IsMatch(key))
                return new MappedValue
                {
                    RawValue = val,
                    NormalizedValue = Normalize(field.DataType, val),
                    Confidence = b.Confidence,
                    SourceRef = $"TextBlock:{b.TextBlockId}"
                };
        }
        return Empty(field);
    }

    private static MappedValue FromRegex(MappingField field, OcrExtraction ex)
    {
        if (field.SourcePattern is null) return Empty(field);
        var rx = new Regex(field.SourcePattern, RegexOptions.IgnoreCase);
        var fullText = string.Join('\n', ex.TextBlocks.Select(b => b.Content));
        var m = rx.Match(fullText);
        if (!m.Success) return Empty(field);

        var captured = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        var conf = ex.TextBlocks.Count > 0
            ? Math.Round(ex.TextBlocks.Where(b => b.Confidence.HasValue).Select(b => b.Confidence!.Value).DefaultIfEmpty(0).Average(), 4)
            : (decimal?)null;

        return new MappedValue
        {
            RawValue = captured,
            NormalizedValue = Normalize(field.DataType, captured),
            Confidence = conf,
            SourceRef = "Regex:fulltext"
        };
    }

    private static MappedValue FromTable(MappingField field, OcrExtraction ex)
    {
        if (field.TableHeader is null) return Empty(field);

        foreach (var table in ex.Tables)
        {
            var headerCell = table.Cells.FirstOrDefault(c =>
                c.IsHeader &&
                string.Equals(c.Content?.Trim(), field.TableHeader, StringComparison.OrdinalIgnoreCase));
            if (headerCell is null) continue;

            int col = headerCell.ColIndex;
            var dataCells = table.Cells
                .Where(c => !c.IsHeader && c.ColIndex == col)
                .OrderBy(c => c.RowIndex)
                .ToList();
            if (dataCells.Count == 0) continue;

            var selected = (field.RowSelector ?? "FIRST") switch
            {
                "LAST" => new[] { dataCells[^1] },
                "ALL"  => dataCells.ToArray(),
                _      => new[] { dataCells[0] }
            };

            var raw = string.Join(" | ", selected.Select(c => c.Content));
            var conf = Math.Round(selected.Where(c => c.Confidence.HasValue)
                                          .Select(c => c.Confidence!.Value)
                                          .DefaultIfEmpty(0).Average(), 4);

            return new MappedValue
            {
                RawValue = raw,
                NormalizedValue = field.RowSelector == "ALL" ? raw : Normalize(field.DataType, raw),
                Confidence = conf,
                SourceRef = $"Table:{table.TableIndex}/Col:{col}"
            };
        }
        return Empty(field);
    }

    private sealed record TableArray(List<Dictionary<string, object?>> Rows, decimal? Confidence, string? SourceRef);

    /// <summary>
    /// Builds an array of typed objects from a table: the field's TableHeader selects which OCR
    /// table to iterate; each sub-column's TableHeader locates its column. Rows chosen by RowSelector.
    /// </summary>
    private TableArray? BuildTableArray(
        MappingField field, OcrExtraction ex, IReadOnlyList<MappingTableColumn> cols, DayMonthOrder order)
    {
        if (field.TableHeader is null) return null;

        foreach (var table in ex.Tables)
        {
            // anchor: the table must contain the field's header
            bool hasAnchor = table.Cells.Any(c => c.IsHeader &&
                string.Equals(c.Content?.Trim(), field.TableHeader, StringComparison.OrdinalIgnoreCase));
            if (!hasAnchor) continue;

            // resolve each sub-column header to a column index within this table
            var colIndex = new Dictionary<int, int>();
            foreach (var col in cols)
            {
                var hc = table.Cells.FirstOrDefault(c => c.IsHeader &&
                    string.Equals(c.Content?.Trim(), col.TableHeader, StringComparison.OrdinalIgnoreCase));
                if (hc is not null) colIndex[col.ColumnId] = hc.ColIndex;
            }
            if (colIndex.Count == 0) continue;

            var rowIndices = table.Cells.Where(c => !c.IsHeader)
                                        .Select(c => c.RowIndex).Distinct().OrderBy(r => r).ToList();
            if (rowIndices.Count == 0) continue;

            var selected = (field.RowSelector ?? "ALL") switch
            {
                "FIRST" => new[] { rowIndices[0] },
                "LAST"  => new[] { rowIndices[^1] },
                _       => rowIndices.ToArray()
            };

            var rows = new List<Dictionary<string, object?>>();
            var confs = new List<decimal>();
            foreach (var ri in selected)
            {
                var obj = new Dictionary<string, object?>();
                foreach (var col in cols)
                {
                    if (!colIndex.TryGetValue(col.ColumnId, out var ci)) { obj[col.TargetSubProperty] = null; continue; }
                    var cell = table.Cells.FirstOrDefault(c => !c.IsHeader && c.RowIndex == ri && c.ColIndex == ci);
                    obj[col.TargetSubProperty] = NormalizeTyped(col.DataType, cell?.Content, order);
                    if (cell?.Confidence is { } cc) confs.Add(cc);
                }
                rows.Add(obj);
            }

            decimal? conf = confs.Count > 0 ? Math.Round(confs.Average(), 4) : null;
            return new TableArray(rows, conf, $"Table:{table.TableIndex}/rows:{rows.Count}");
        }
        return null;
    }

    /// <summary>Normalizes a cell into a typed value per the sub-column's DataType (reuses TextNormalizer).</summary>
    private object? NormalizeTyped(string dataType, string? raw, DayMonthOrder order)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        switch (dataType?.ToUpperInvariant())
        {
            case "DECIMAL":
                return normalizer.TryNormalizeNumber(raw, out var d) ? d : raw.Trim();
            case "INT":
                return normalizer.TryNormalizeNumber(raw, out var n) ? (long)n : raw.Trim();
            case "BOOL":
                var t = TextNormalizer.NormalizeThaiDigits(raw).Trim().ToLowerInvariant();
                return t is "1" or "true" or "yes" or "y";
            case "DATE":
                return normalizer.TryNormalizeDate(raw, order, out var dt)
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : raw.Trim();
            default: // STRING
                return TextNormalizer.NormalizeThaiDigits(raw).Trim();
        }
    }

    private static string? Normalize(string dataType, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return dataType switch
        {
            "DECIMAL" => decimal.TryParse(v.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                            ? d.ToString(CultureInfo.InvariantCulture) : v,
            "INT"     => int.TryParse(v.Replace(",", ""), out var i) ? i.ToString() : v,
            "DATE"    => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                            ? dt.ToString("yyyy-MM-dd") : v,
            "BOOL"    => (v is "1" or "true" or "yes" or "Y").ToString(),
            _         => v
        };
    }

    private static MappedValue Empty(MappingField field) => new()
    {
        RawValue = null,
        NormalizedValue = field.DefaultValue,
        Confidence = 0m
    };
}
