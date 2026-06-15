using Microsoft.Extensions.Logging.Abstractions;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Normalization;
using OcrPipeline.Web.Services.Ocr;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline test (no DB, no tessdata): a fake OCR engine throws during EXTRACT and we assert the
/// pipeline marks the document FAILED and logs a FAILED PipelineEvent instead of propagating.
/// </summary>
public sealed class PipelineFailureTests
{
    // OCR engine that fails the way Tesseract does with no tessdata installed.
    private sealed class ThrowingOcrEngine : IOcrEngine
    {
        public string Name => "THROWS";
        public Task<OcrExtraction> ExtractAsync(string filePath, string contentType, string? languages = null, CancellationToken ct = default)
            => throw new DirectoryNotFoundException("tessdata folder not found at 'tessdata'.");
    }

    // Fake of the IDocumentRepository seam — records status/event writes, opens no connection.
    private sealed class FakeDocumentRepository(Document doc) : IDocumentRepository
    {
        public string? Status { get; private set; } = doc.StatusCode;
        public List<(string Stage, string To, string? Message)> Events { get; } = [];

        public Document? GetById(long id) => doc;
        public void SetClassification(long id, int typeId, decimal conf) { doc.DocumentTypeId = typeId; Status = "CLASSIFIED"; }
        public void SetStatus(long id, string statusCode) { Status = statusCode; doc.StatusCode = statusCode; }
        public void LogEvent(long id, string stage, string? from, string to, string? message, int? byUserId)
            => Events.Add((stage, to, message));

        // not reached on the failure-at-EXTRACT path
        public long Insert(Document d) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetRecent(int top = 50) => throw new NotSupportedException();
        public IReadOnlyList<DocumentRef> GetByTypeWithPreviews(int documentTypeId, int top = 20) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetByStatus(string statusCode) => throw new NotSupportedException();
        public void InsertPages(long id, IEnumerable<DocumentPage> pages) => throw new NotSupportedException();
        public IReadOnlyList<DocumentPage> GetPages(long id) => throw new NotSupportedException();
    }

    // Minimal IMappingRepository: no active template, so the pipeline takes the OCR-first path and
    // EXTRACT (the throwing engine) is reached. No DB connection is opened.
    private sealed class NullMappingRepository : IMappingRepository
    {
        public MappingTemplate? GetActiveTemplateForType(int documentTypeId) => null;
        public Dictionary<int, List<TransformerStep>> GetTransformerSteps(int t) => throw new NotSupportedException();
        public Dictionary<int, List<MappingTableColumn>> GetTableColumns(int t) => throw new NotSupportedException();
        public void SaveTableColumns(int fieldId, IEnumerable<MappingTableColumn> c) => throw new NotSupportedException();
        public IReadOnlyList<(MappingTemplate tpl, string docType, int fieldCount)> GetAllTemplates() => throw new NotSupportedException();
        public MappingTemplate? GetTemplateById(int t) => throw new NotSupportedException();
        public IReadOnlyList<string> GetPropertyKeysForType(int t) => throw new NotSupportedException();
        public void SaveFields(int t, IEnumerable<MappingField> f, IReadOnlyDictionary<int, List<TransformerStep>> s) => throw new NotSupportedException();
        public int UpsertFieldBinding(int t, MappingField f, bool b) => throw new NotSupportedException();
        public void SaveZones(int t, string mappingMode, IEnumerable<MappingField> f) => throw new NotSupportedException();
        public int SaveTableZone(int t, MappingField f, IEnumerable<MappingTableColumn> c) => throw new NotSupportedException();
        public long SaveResult(long d, MappingOutcome o) => throw new NotSupportedException();
        public (decimal? overall, bool needsReview, string? json, List<MappedValueRow> values)? GetLatestResult(long d) => throw new NotSupportedException();
        public int UpdateResultValue(long d, long rv, string? n) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Extraction_failure_marks_document_FAILED_and_logs_event()
    {
        var doc = new Document { DocumentId = 42, StatusCode = "CAPTURED", StoredPath = "x.png", ContentType = "image/png" };
        var docs = new FakeDocumentRepository(doc);

        var unused = new SqlConnectionFactory("unused"); // never used: extraction throws before any DB call
        var ocrRepo = new OcrRepository(unused);
        var mappingRepo = new NullMappingRepository(); // no active template -> OCR-first path, EXTRACT throws
        var extraction = new ExtractionService(new ThrowingOcrEngine(), ocrRepo);
        var mappingEngine = new MappingEngine(new TransformerPipeline(Array.Empty<IValueTransformer>()), new TextNormalizer());

        var pipeline = new PipelineService(
            docs, ocrRepo, mappingRepo, extraction, mappingEngine, zonal: null!, NullLogger<PipelineService>.Instance);

        // must not throw
        await pipeline.ProcessAsync(doc.DocumentId, byUserId: null);

        Assert.Equal("FAILED", docs.Status);
        var failure = Assert.Single(docs.Events, e => e.To == "FAILED");
        Assert.Equal("EXTRACT", failure.Stage);
        Assert.StartsWith("Extraction failed:", failure.Message);
        Assert.DoesNotContain("\n", failure.Message);   // single line, no stack trace
    }
}
