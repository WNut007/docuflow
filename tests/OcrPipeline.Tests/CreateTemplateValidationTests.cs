using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using OcrPipeline.Web.Controllers;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline tests for MappingController.CreateTemplate's document-type guard: a submitted
/// DocumentTypeId that isn't an ACTIVE type must be rejected (no template created), while a valid
/// active id creates the template and opens the zone designer. This mirrors the New-template
/// dropdown's orphan-proofing on the server, so a forged/stale POST can't slip a bad id past the FK.
/// Driven through the IMappingRepository fake — no DB.
/// </summary>
public sealed class CreateTemplateValidationTests
{
    private sealed class FakeMapping : IMappingRepository
    {
        public List<(int dt, string name)> Created { get; } = [];
        private static readonly IReadOnlyList<(int Id, string Name)> ActiveTypes =
            [(1, "Invoice"), (2, "Receipt"), (3, "Purchase Order"), (4, "Contract")];

        public IReadOnlyList<(int Id, string Name)> GetDocumentTypes() => ActiveTypes;
        public int CreateTemplate(int dt, string name, string model, string mode)
        { Created.Add((dt, name)); return 99; }

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

    private static MappingController NewController(FakeMapping m) =>
        new(m, new StubDocs())
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    [Theory]
    [InlineData(11)]   // id with no DocumentType row (would FK-fail)
    [InlineData(0)]    // empty/unselected dropdown binds to 0
    [InlineData(99)]   // valid-shape but inactive/nonexistent
    public void Invalid_document_type_is_rejected_and_creates_nothing(int badId)
    {
        var mapping = new FakeMapping();

        var result = NewController(mapping)
            .CreateTemplate(documentTypeId: badId, name: "Some template", mappingMode: "ZONAL", targetModel: null);

        Assert.Empty(mapping.Created);                                          // no orphan template created
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MappingController.Index), redirect.ActionName);     // bounced back to the list
    }

    [Fact]
    public void Valid_document_type_creates_template_and_opens_designer()
    {
        var mapping = new FakeMapping();

        var result = NewController(mapping)
            .CreateTemplate(documentTypeId: 1, name: "Acme Invoice", mappingMode: "ZONAL", targetModel: null);

        var created = Assert.Single(mapping.Created);
        Assert.Equal(1, created.dt);
        Assert.Equal("Acme Invoice", created.name);
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(MappingController.Zones), redirect.ActionName);     // straight into the zone designer
    }
}
