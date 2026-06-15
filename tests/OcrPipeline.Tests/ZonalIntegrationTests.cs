using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Imaging;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;
using OcrPipeline.Web.Services.Zonal;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// End-to-end zonal extraction against the REAL east-repair sample (crop -> region OCR -> normalize),
/// proving the core bet: drawn zones read clean values where full-page OCR produced merged blobs.
/// SELF-SKIPPING: passes trivially when tessdata/native isn't present (bare CI); runs for real on a
/// dev machine. Zones use the hand-measured bboxes from DevController.
/// </summary>
public sealed class ZonalIntegrationTests
{
    [Fact]
    public async Task Zonal_reads_clean_values_from_the_sample_invoice()
    {
        string? tessdata = FindUp("tessdata", isDir: true) is { } d && File.Exists(Path.Combine(d, "eng.traineddata")) ? d : null;
        string? sample = FindUp(Path.Combine("samples", "east-repair-invoice.png"), isDir: false);
        if (tessdata is null || sample is null) return; // skip where the environment lacks tessdata/sample

        var opts = Options.Create(new TesseractOptions
        {
            TessdataPath = tessdata, Languages = "eng", Dpi = 300, MinOcrWidth = 2200
        });
        var preprocessor = new ImagePreprocessor();
        var normalizer = new TextNormalizer();
        var tesseract = new TesseractOcrEngine(opts, preprocessor, normalizer);
        var engine = new MappingEngine(new TransformerPipeline(System.Array.Empty<IValueTransformer>()), normalizer);
        var svc = new ZonalExtractionService(tesseract, preprocessor, new PagePreviewRenderer(), engine, normalizer, opts);

        var template = new MappingTemplate
        {
            TemplateId = 1, TargetModel = "InvoiceModel", MappingMode = "ZONAL",
            Fields =
            {
                // measured against samples/east-repair-invoice.png (750x1061) via horizontal projection
                Zone(1, "invoice_id",   "STRING",  "TEXT",    0.815m, 0.225m, 0.135m, 0.020m),
                Zone(2, "invoice_date", "DATE",    "DATE",    0.795m, 0.250m, 0.150m, 0.020m),
                Zone(3, "due_date",     "DATE",    "DATE",    0.795m, 0.297m, 0.150m, 0.020m),
                Zone(4, "total",        "DECIMAL", "NUMERIC", 0.810m, 0.551m, 0.135m, 0.022m),
            }
        };

        var doc = new Document { DocumentId = 1, StoredPath = sample, ContentType = "image/png", OcrLanguages = "eng" };

        MappingOutcome outcome;
        try
        {
            outcome = await svc.ProcessAsync(doc, template, new Dictionary<int, List<TransformerStep>>(), default);
        }
        catch (System.Exception ex) when (ex is System.DllNotFoundException or System.BadImageFormatException)
        {
            return; // native libtesseract not loadable here -> skip
        }

        Assert.Equal("US-001", Val(outcome, "invoice_id"));
        Assert.Equal("2019-02-11", Val(outcome, "invoice_date"));  // dd/MM inferred (26/02 proves day-first)
        Assert.Equal("2019-02-26", Val(outcome, "due_date"));
        Assert.Equal("154.06", Val(outcome, "total"));
    }

    private static MappingField Zone(int id, string prop, string dt, string hint, decimal x, decimal y, decimal w, decimal h)
        => new()
        {
            FieldId = id, TargetProperty = prop, DataType = dt, MinConfidence = 0.60m, SourceType = "KEY_VALUE",
            ZonePage = 1, ZoneX = x, ZoneY = y, ZoneW = w, ZoneH = h, ZoneOcrHint = hint
        };

    private static string? Val(MappingOutcome o, string prop) =>
        o.Values.Single(v => v.TargetProperty == prop).NormalizedValue;

    private static string? FindUp(string relative, bool isDir)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (isDir ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
