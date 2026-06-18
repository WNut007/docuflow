using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using OcrPipeline.Web.Controllers;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline tests for MappingController.CreateTemplate's guards: a non-active document type or a
/// missing sample file must be rejected (nothing created/bound), while valid input creates the
/// template, stores the sample as a SAMPLE backdrop (not processed), binds it, and opens the
/// designer. Mirrors the New-template form's orphan-proofing + required-sample on the server.
/// Driven through fakes - no DB, no file system.
/// </summary>
public sealed class CreateTemplateValidationTests
{
    private sealed class FakeMapping : IMappingRepository
    {
        public List<(int dt, string name)> Created { get; } = [];
        public (int tpl, long doc)? Bound { get; private set; }
        private static readonly IReadOnlyList<(int Id, string Name)> ActiveTypes =
            [(1, "Invoice"), (2, "Receipt"), (3, "Purchase Order"), (4, "Contract")];

        public IReadOnlyList<(int Id, string Name)> GetDocumentTypes() => ActiveTypes;
        public int CreateTemplate(int dt, string name, string model, string mode) { Created.Add((dt, name)); return 99; }
        public void SetTemplateSample(int templateId, long documentId) => Bound = (templateId, documentId);

        // not exercised by CreateTemplate
        public Dictionary<int, List<TransformerStep>> GetTransformerSteps(int t) => throw new NotSupportedException();
        public Dictionary<int, List<MappingTableColumn>> GetTableColumns(int t) => throw new NotSupportedException();
        public void SaveTableColumns(int fieldId, IEnumerable<MappingTableColumn> columns) => throw new NotSupportedException();
        public MappingTemplate? GetActiveTemplateForType(int t) => throw new NotSupportedException();
        public IReadOnlyList<TemplateResolver.Candidate> GetTemplatesForType(int t) => throw new NotSupportedException();
        public IReadOnlyList<(MappingTemplate tpl, string docType, int fieldCount)> GetAllTemplates() => throw new NotSupportedException();
        public MappingTemplate? GetTemplateById(int t) => throw new NotSupportedException();
        public IReadOnlyList<string> GetPropertyKeysForType(int t) => throw new NotSupportedException();
        public void SaveFields(int t, IEnumerable<MappingField> f, IReadOnlyDictionary<int, List<TransformerStep>> s) => throw new NotSupportedException();
        public void SaveZones(int t, string mappingMode, IEnumerable<MappingField> f) => throw new NotSupportedException();
        public int SaveTableZone(int t, MappingField f, IEnumerable<MappingTableColumn> c) => throw new NotSupportedException();
        public int DeleteZoneFields(int t, IEnumerable<int> ids) => throw new NotSupportedException();
        public int UpsertFieldBinding(int templateId, MappingField f, bool bindingChanged) => throw new NotSupportedException();
        public long SaveResult(long d, MappingOutcome o) => throw new NotSupportedException();
        public (decimal? overall, bool needsReview, string? json, int templateId, List<MappedValueRow> values)? GetLatestResult(long d) => throw new NotSupportedException();
        public int UpdateResultValue(long d, long rv, string? n) => throw new NotSupportedException();
    }

    private sealed class FakeIngestion : IDocumentIngestionService
    {
        public List<string> Channels { get; } = [];
        public Task<long> StoreAndRasterizeAsync(IFormFile file, string sourceChannel, string statusCode,
            int? templateId, int? userId, string? ocrLanguages, CancellationToken ct = default)
        { Channels.Add(sourceChannel); return Task.FromResult(777L); }
    }

    private sealed class StubDocs : IDocumentRepository
    {
        public Document? GetById(long id) => throw new NotSupportedException();
        public long Insert(Document d) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetRecent(int top = 50) => throw new NotSupportedException();
        public IReadOnlyList<DocumentRef> GetByTypeWithPreviews(int t, int top = 20) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetByStatus(string statusCode) => throw new NotSupportedException();
        public void InsertPages(long id, IEnumerable<DocumentPage> p) => throw new NotSupportedException();
        public IReadOnlyList<DocumentPage> GetPages(long id) => throw new NotSupportedException();
        public void SetClassification(long id, int t, decimal c) => throw new NotSupportedException();
        public void SetStatus(long id, string s) => throw new NotSupportedException();
        public void LogEvent(long id, string st, string? f, string to, string? m, int? u) => throw new NotSupportedException();
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private static MappingController NewController(FakeMapping m, FakeIngestion ing)
    {
        var http = new DefaultHttpContext();   // non-null User (empty principal) so GetUserId() returns null cleanly
        return new(m, new StubDocs(), ing)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NullTempDataProvider())
        };
    }

    private static IFormFile Sample()
    {
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };   // "%PDF"
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "sample", "sample.pdf")
        { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
    }

    [Theory]
    [InlineData(11)]   // id with no DocumentType row (would FK-fail)
    [InlineData(0)]    // empty/unselected dropdown binds to 0
    [InlineData(99)]   // valid-shape but inactive/nonexistent
    public async Task Invalid_document_type_is_rejected_and_creates_nothing(int badId)
    {
        var mapping = new FakeMapping();

        var result = await NewController(mapping, new FakeIngestion())
            .CreateTemplate(badId, name: "Some template", mappingMode: "ZONAL", targetModel: null, sample: Sample(), default);

        Assert.Empty(mapping.Created);                                          // no orphan template
        Assert.Null(mapping.Bound);                                             // nothing bound
        Assert.Equal(nameof(MappingController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
    }

    [Fact]
    public async Task Missing_sample_is_rejected_and_creates_nothing()
    {
        var mapping = new FakeMapping();

        var result = await NewController(mapping, new FakeIngestion())
            .CreateTemplate(1, name: "Acme Invoice", mappingMode: "ZONAL", targetModel: null, sample: null, default);

        Assert.Empty(mapping.Created);                                          // sample is required
        Assert.Null(mapping.Bound);
        Assert.Equal(nameof(MappingController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
    }

    [Fact]
    public async Task Valid_input_creates_template_stores_sample_and_opens_designer()
    {
        var mapping = new FakeMapping();
        var ingestion = new FakeIngestion();

        var result = await NewController(mapping, ingestion)
            .CreateTemplate(1, name: "Acme Invoice", mappingMode: "ZONAL", targetModel: null, sample: Sample(), default);

        var created = Assert.Single(mapping.Created);
        Assert.Equal(1, created.dt);
        Assert.Equal("SAMPLE", Assert.Single(ingestion.Channels));             // stored as backdrop, never processed
        Assert.Equal(99, mapping.Bound!.Value.tpl);                            // sample bound to the new template id
        Assert.Equal(777L, mapping.Bound!.Value.doc);
        Assert.Equal(nameof(MappingController.Zones), Assert.IsType<RedirectToActionResult>(result).ActionName);
    }
}
