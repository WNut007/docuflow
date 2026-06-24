using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
/// Offline tests for MappingController.Zones (sub-step 3): the zone designer draws on the template's
/// OWN bound sample document (MappingTemplate.SampleDocumentId), with no doc-picker. A template with a
/// sample resolves DocumentId + PageCount from that bound doc; a template with no sample yields a null
/// DocumentId (the view's empty-state) and never touches the document repo. Driven through fakes - no
/// DB. Guards the GetTemplateById-hydration + bound-sample path against regressing back to the
/// type-wide doc picker.
/// </summary>
public sealed class ZonesDesignerSampleTests
{
    private sealed class FakeMapping(MappingTemplate? template) : IMappingRepository
    {
        public MappingTemplate? GetTemplateById(int t) => template?.TemplateId == t ? template : null;
        public IReadOnlyList<(MappingTemplate tpl, string docType, int fieldCount)> GetAllTemplates() =>
            template is null ? [] : [(template, "Invoice", template.Fields.Count)];
        public Dictionary<int, List<MappingTableColumn>> GetTableColumns(int t) => new();

        // not exercised by Zones
        public IReadOnlyList<(int Id, string Name)> GetDocumentTypes() => throw new NotSupportedException();
        public int CreateTemplate(int dt, string name, string model, string mode) => throw new NotSupportedException();
        public void SetTemplateSample(int templateId, long documentId) => throw new NotSupportedException();
        public Dictionary<int, List<TransformerStep>> GetTransformerSteps(int t) => throw new NotSupportedException();
        public void SaveTableColumns(int fieldId, IEnumerable<MappingTableColumn> columns) => throw new NotSupportedException();
        public MappingTemplate? GetActiveTemplateForType(int t) => throw new NotSupportedException();
        public IReadOnlyList<TemplateResolver.Candidate> GetTemplatesForType(int t) => throw new NotSupportedException();
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

    private sealed class FakeDocs(Document? byId) : IDocumentRepository
    {
        public List<long> GetByIdCalls { get; } = [];
        public Document? GetById(long id) { GetByIdCalls.Add(id); return byId?.DocumentId == id ? byId : null; }

        // the bound-sample path must NOT fall back to the type-wide picker
        public IReadOnlyList<DocumentRef> GetByTypeWithPreviews(int t, int top = 20) => throw new NotSupportedException();
        public long Insert(Document d) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetRecent(int top = 50) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetByStatus(string statusCode) => throw new NotSupportedException();
        public void InsertPages(long id, IEnumerable<DocumentPage> p) => throw new NotSupportedException();
        public IReadOnlyList<DocumentPage> GetPages(long id) => throw new NotSupportedException();
        public void SetClassification(long id, int t, decimal c) => throw new NotSupportedException();
        public void SetStatus(long id, string s) => throw new NotSupportedException();
        public void LogEvent(long id, string st, string? f, string to, string? m, int? u) => throw new NotSupportedException();
    }

    private sealed class StubIngestion : IDocumentIngestionService
    {
        public Task<long> StoreAndRasterizeAsync(IFormFile file, string sourceChannel, string statusCode,
            int? templateId, int? userId, string? ocrLanguages, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static ZoneDesignerViewModel RunZones(MappingTemplate template, Document? sampleDoc, out FakeDocs docs)
    {
        docs = new FakeDocs(sampleDoc);
        var controller = new MappingController(new FakeMapping(template), docs, new StubIngestion(),
            new OcrPipeline.Web.Services.Zonal.NullTableLayoutDetector());
        var result = controller.Zones(template.TemplateId);
        return Assert.IsType<ZoneDesignerViewModel>(Assert.IsType<ViewResult>(result).Model);
    }

    [Fact]
    public void Designer_draws_on_the_templates_bound_sample()
    {
        var tpl = new MappingTemplate { TemplateId = 9, Name = "test_form_1", MappingMode = "ZONAL",
            DocumentTypeId = 1, SampleDocumentId = 49 };
        var sample = new Document { DocumentId = 49, PageCount = 3, StatusCode = "SAMPLE", SourceChannel = "SAMPLE" };

        var vm = RunZones(tpl, sample, out var docs);

        Assert.Equal(49, vm.DocumentId);             // the bound sample, not a type-wide pick
        Assert.Equal(3, vm.PageCount);               // pager driven by the bound doc's page count
        Assert.Equal(49, Assert.Single(docs.GetByIdCalls));
    }

    [Fact]
    public void Sampleless_template_yields_empty_state_and_no_doc_lookup()
    {
        var tpl = new MappingTemplate { TemplateId = 7, Name = "legacy", MappingMode = "ZONAL",
            DocumentTypeId = 1, SampleDocumentId = null };

        var vm = RunZones(tpl, sampleDoc: null, out var docs);

        Assert.Null(vm.DocumentId);                  // -> view shows the "no sample yet" empty-state
        Assert.Equal(0, vm.PageCount);
        Assert.Empty(docs.GetByIdCalls);             // never hits the document repo when unbound
    }

    // ---- DetectTables (Option 3B auto-columns endpoint) -----------------------

    private sealed class StubDetector(OcrPipeline.Web.Services.Zonal.ColumnDetectionResult result)
        : OcrPipeline.Web.Services.Zonal.ITableLayoutDetector
    {
        public int Calls; public long DocId; public int Page; public OcrPipeline.Web.Services.Zonal.RectN Zone;
        public Task<OcrPipeline.Web.Services.Zonal.ColumnDetectionResult> DetectColumnsAsync(
            long documentId, int page, OcrPipeline.Web.Services.Zonal.RectN zone, CancellationToken ct = default)
        { Calls++; DocId = documentId; Page = page; Zone = zone; return Task.FromResult(result); }
    }

    private static MappingController NewDetectController(MappingTemplate tpl, OcrPipeline.Web.Services.Zonal.ITableLayoutDetector detector)
        => new(new FakeMapping(tpl), new FakeDocs(null), new StubIngestion(), detector);

    private static object Val(IActionResult r) => Assert.IsType<JsonResult>(r).Value!;
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    [Fact]
    public async Task DetectTables_with_no_sample_returns_a_note_and_never_calls_the_detector()
    {
        var tpl = new MappingTemplate { TemplateId = 7, MappingMode = "ZONAL", DocumentTypeId = 1, SampleDocumentId = null };
        var det = new StubDetector(OcrPipeline.Web.Services.Zonal.ColumnDetectionResult.Empty("x"));
        var c = NewDetectController(tpl, det);

        var v = Val(await c.DetectTables(new DetectTablesPayload { TemplateId = 7, Page = 1, ZoneX = 0.1, ZoneY = 0.1, ZoneW = 0.5, ZoneH = 0.3 }, default));

        Assert.Equal(0, det.Calls);                                  // no sample => no detection attempt
        Assert.Equal(0, Prop(v, "columnCount"));
        Assert.Contains("no sample", ((string?)Prop(v, "note"))!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectTables_with_a_degenerate_zone_returns_a_note_and_never_calls_the_detector()
    {
        var tpl = new MappingTemplate { TemplateId = 9, MappingMode = "ZONAL", DocumentTypeId = 1, SampleDocumentId = 49 };
        var det = new StubDetector(OcrPipeline.Web.Services.Zonal.ColumnDetectionResult.Empty("x"));
        var c = NewDetectController(tpl, det);

        var v = Val(await c.DetectTables(new DetectTablesPayload { TemplateId = 9, Page = 1, ZoneW = 0, ZoneH = 0 }, default));

        Assert.Equal(0, det.Calls);                                  // empty/whole-page crop never posted
        Assert.Contains("Draw a table zone", ((string?)Prop(v, "note"))!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectTables_delegates_to_the_detector_on_the_server_resolved_sample()
    {
        var tpl = new MappingTemplate { TemplateId = 9, MappingMode = "ZONAL", DocumentTypeId = 1, SampleDocumentId = 49 };
        var det = new StubDetector(new OcrPipeline.Web.Services.Zonal.ColumnDetectionResult(new[] { 0.3, 0.6 }, 3, null));
        var c = NewDetectController(tpl, det);

        var v = Val(await c.DetectTables(new DetectTablesPayload { TemplateId = 9, Page = 2, ZoneX = 0.1, ZoneY = 0.2, ZoneW = 0.5, ZoneH = 0.3 }, default));

        Assert.Equal(1, det.Calls);
        Assert.Equal(49L, det.DocId);                                // sample resolved from the template, NOT the client
        Assert.Equal(2, det.Page);
        Assert.Equal(0.5, det.Zone.W, 6);
        Assert.Equal(3, Prop(v, "columnCount"));
        Assert.Equal(new[] { 0.3, 0.6 }, (IReadOnlyList<double>)Prop(v, "boundaries")!);
        Assert.Null(Prop(v, "note"));
    }
}
