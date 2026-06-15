using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;
using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline zonal-resolve tests (no DB, no Tesseract, no images): drive ZonalExtractionService.BuildAsync
/// with a FAKE per-zone OCR delegate supplying canned (text, confidence) per field. Asserts the values
/// are normalized via the SAME path as OCR-first (dd/MM date inference, decimal), transformers run, and
/// NeedsReview is set on low confidence or an empty required field.
/// </summary>
public sealed class ZonalResolveTests
{
    private static ZonalExtractionService NewService()
    {
        var engine = new MappingEngine(
            new TransformerPipeline(new IValueTransformer[] { new TrimTransformer() }),
            new TextNormalizer());
        // image deps are unused by BuildAsync
        return new ZonalExtractionService(null!, null!, null!, engine, Options.Create(new TesseractOptions()));
    }

    private static MappingField Field(int id, string prop, string dataType, bool required = false) => new()
    {
        FieldId = id, TargetProperty = prop, DataType = dataType, IsRequired = required,
        MinConfidence = 0.60m, SourceType = "KEY_VALUE",
        ZoneX = 0.1m, ZoneY = 0.1m, ZoneW = 0.1m, ZoneH = 0.05m, ZonePage = 1, ZoneOcrHint = "TEXT"
    };

    private static Func<MappingField, Task<(string, decimal)>> Canned(Dictionary<string, (string, decimal)> map)
        => f => Task.FromResult(map[f.TargetProperty]);

    [Fact]
    public async Task Resolves_and_normalizes_each_zone()
    {
        var template = new MappingTemplate
        {
            TemplateId = 1, TargetModel = "InvoiceModel", MappingMode = "ZONAL",
            Fields =
            {
                Field(1, "invoice_id", "STRING", required: true),
                Field(2, "invoice_date", "DATE"),
                Field(3, "due_date", "DATE"),
                Field(4, "total", "DECIMAL"),
            }
        };
        var ocr = Canned(new()
        {
            ["invoice_id"] = ("US-001", 0.95m),
            ["invoice_date"] = ("11/02/2019", 0.90m),   // due date 26/02 proves day-first -> 2019-02-11
            ["due_date"] = ("26/02/2019", 0.90m),
            ["total"] = ("154.06", 0.92m),
        });
        // trim transformer on total (FieldId 4) — exercises the pipeline path
        var steps = new Dictionary<int, List<TransformerStep>> { [4] = new() { new TransformerStep { Type = "trim" } } };

        var outcome = await NewService().BuildAsync(template, ocr, steps, default);

        Assert.Equal("US-001", Val(outcome, "invoice_id"));
        Assert.Equal("2019-02-11", Val(outcome, "invoice_date"));
        Assert.Equal("2019-02-26", Val(outcome, "due_date"));
        Assert.Equal("154.06", Val(outcome, "total"));
        Assert.False(outcome.NeedsReview);
        Assert.NotNull(outcome.OverallConfidence);
    }

    [Fact]
    public async Task Low_confidence_zone_flags_review()
    {
        var template = new MappingTemplate
        {
            TemplateId = 1, MappingMode = "ZONAL",
            Fields = { Field(1, "invoice_id", "STRING", required: true) }
        };
        var ocr = Canned(new() { ["invoice_id"] = ("US-001", 0.30m) }); // below MinConfidence 0.60

        var outcome = await NewService().BuildAsync(template, ocr, new Dictionary<int, List<TransformerStep>>(), default);

        Assert.True(outcome.NeedsReview);
        Assert.True(outcome.Values.Single(v => v.TargetProperty == "invoice_id").IsBelowThreshold);
    }

    [Fact]
    public async Task Empty_required_zone_flags_review()
    {
        var template = new MappingTemplate
        {
            TemplateId = 1, MappingMode = "ZONAL",
            Fields = { Field(1, "invoice_id", "STRING", required: true) }
        };
        var ocr = Canned(new() { ["invoice_id"] = ("", 0.90m) }); // confident but empty

        var outcome = await NewService().BuildAsync(template, ocr, new Dictionary<int, List<TransformerStep>>(), default);

        Assert.True(outcome.NeedsReview);
        Assert.True(string.IsNullOrEmpty(Val(outcome, "invoice_id")));
    }

    [Fact]
    public async Task Skips_fields_without_a_zone()
    {
        var withZone = Field(1, "invoice_id", "STRING");
        var noZone = Field(2, "po_number", "STRING");
        noZone.ZoneX = null; // not drawn
        var template = new MappingTemplate { TemplateId = 1, MappingMode = "ZONAL", Fields = { withZone, noZone } };

        bool poRequested = false;
        Func<MappingField, Task<(string, decimal)>> ocr = f =>
        {
            if (f.TargetProperty == "po_number") poRequested = true;
            return Task.FromResult(("US-001", 0.95m));
        };

        var outcome = await NewService().BuildAsync(template, ocr, new Dictionary<int, List<TransformerStep>>(), default);

        Assert.False(poRequested);                          // un-zoned field never OCR'd
        Assert.Equal("US-001", Val(outcome, "invoice_id"));
    }

    private static string? Val(MappingOutcome o, string prop) =>
        o.Values.Single(v => v.TargetProperty == prop).NormalizedValue;
}
