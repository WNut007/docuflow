using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Locks the Phase-3 single-emitter rule in <see cref="MappingEngine.RunZonalAsync"/>: when a
/// line_item table is defined by several role-tagged TABLE_CELL fields sharing one TargetProperty,
/// the property is emitted ONCE (by the canonical field carrying the concatenated rows); sibling
/// region fields must not overwrite it with an empty default. Pure: no Tesseract, no DB.
/// </summary>
public sealed class RunZonalEmitterTests
{
    private static MappingEngine Engine() =>
        new(new TransformerPipeline(System.Array.Empty<IValueTransformer>()), new TextNormalizer());

    [Fact]
    public async Task Sibling_region_table_fields_emit_one_line_item_value()
    {
        var template = new MappingTemplate
        {
            TemplateId = 1, TargetModel = "InvoiceModel", MappingMode = "ZONAL",
            Fields =
            {
                new MappingField { FieldId = 10, TargetProperty = "line_item", SourceType = "TABLE_CELL", ZonePageRole = "FIRST", IsRequired = true, MinConfidence = 0.4m },
                new MappingField { FieldId = 11, TargetProperty = "line_item", SourceType = "TABLE_CELL", ZonePageRole = "LAST",  IsRequired = true, MinConfidence = 0.4m },
            }
        };

        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["qty"] = 1L, ["description"] = "a" },
            new() { ["qty"] = 2L, ["description"] = "b" },
        };
        // concatenated rows supplied once under the canonical (FIRST) field id 10; field 11 absent.
        var tableResults = new Dictionary<int, (List<Dictionary<string, object?>> Rows, decimal? Conf)>
        {
            [10] = (rows, 0.90m),
        };

        var outcome = await Engine().RunZonalAsync(
            template,
            new Dictionary<int, (string Raw, decimal Conf)>(),       // no scalar zones
            new Dictionary<int, List<TransformerStep>>(),
            tableResults);

        var emitted = outcome.Values.Where(v => v.TargetProperty == "line_item").ToList();
        Assert.Single(emitted);                          // sibling did NOT add a second value
        Assert.Equal(10, emitted[0].FieldId);            // the canonical emitter
        Assert.NotNull(emitted[0].NormalizedValue);
        Assert.Contains("\"qty\"", emitted[0].NormalizedValue!);   // rows survived, not overwritten by a default
        Assert.False(outcome.NeedsReview);               // required-field check tolerates the skipped sibling
    }

    [Fact]
    public async Task Legacy_single_table_field_emits_normally_unaffected_by_the_guard()
    {
        // No page roles, one TABLE_CELL field (the validated single-page shape) -> emits its rows.
        var template = new MappingTemplate
        {
            TemplateId = 1, TargetModel = "InvoiceModel", MappingMode = "ZONAL",
            Fields = { new MappingField { FieldId = 5, TargetProperty = "line_item", SourceType = "TABLE_CELL", MinConfidence = 0.4m } }
        };
        var rows = new List<Dictionary<string, object?>> { new() { ["qty"] = 3L, ["description"] = "x" } };
        var tableResults = new Dictionary<int, (List<Dictionary<string, object?>> Rows, decimal? Conf)> { [5] = (rows, 0.95m) };

        var outcome = await Engine().RunZonalAsync(
            template, new Dictionary<int, (string Raw, decimal Conf)>(),
            new Dictionary<int, List<TransformerStep>>(), tableResults);

        var li = outcome.Values.Single(v => v.TargetProperty == "line_item");
        Assert.Equal(5, li.FieldId);
        Assert.Contains("\"qty\"", li.NormalizedValue!);
    }
}
