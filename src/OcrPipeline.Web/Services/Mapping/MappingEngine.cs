using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Transform;

namespace OcrPipeline.Web.Services.Mapping;

/// <summary>
/// Resolves a MappingTemplate against an OcrExtraction to produce the target model.
/// Each field's SourceType decides how its value is pulled from the OCR artifacts;
/// an optional transformer pipeline then preprocesses the value before mapping.
/// </summary>
public sealed class MappingEngine(TransformerPipeline transformerPipeline)
{
    public async Task<MappingOutcome> RunAsync(
        MappingTemplate template,
        OcrExtraction extraction,
        IReadOnlyDictionary<int, List<TransformerStep>> stepsByField,
        CancellationToken ct = default)
    {
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
