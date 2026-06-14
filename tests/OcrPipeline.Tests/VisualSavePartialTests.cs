using System.Text.RegularExpressions;
using OcrPipeline.Web.Controllers;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Models;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline tests proving the visual mapper's save is a PARTIAL upsert: a payload that only changes
/// field A must not touch field B (no clobbering of B's KeyPattern/TableHeader). Driven through the
/// IMappingRepository fake — no DB.
/// </summary>
public sealed class VisualSavePartialTests
{
    private sealed record Upsert(int FieldId, string TargetProperty, string? KeyPattern, string? TableHeader, bool BindingChanged);

    private sealed class FakeMappingRepository : IMappingRepository
    {
        public List<Upsert> Upserts { get; } = [];
        public List<int> TableColumnSaves { get; } = [];

        public int UpsertFieldBinding(int templateId, MappingField f, bool bindingChanged)
        {
            int id = f.FieldId > 0 ? f.FieldId : 1000 + Upserts.Count;
            Upserts.Add(new Upsert(id, f.TargetProperty, f.KeyPattern, f.TableHeader, bindingChanged));
            return id;
        }
        public void SaveTableColumns(int fieldId, IEnumerable<MappingTableColumn> columns) => TableColumnSaves.Add(fieldId);

        // not used by VisualSave
        public Dictionary<int, List<TransformerStep>> GetTransformerSteps(int t) => throw new NotSupportedException();
        public Dictionary<int, List<MappingTableColumn>> GetTableColumns(int t) => throw new NotSupportedException();
        public MappingTemplate? GetActiveTemplateForType(int t) => throw new NotSupportedException();
        public IReadOnlyList<(MappingTemplate tpl, string docType, int fieldCount)> GetAllTemplates() => throw new NotSupportedException();
        public MappingTemplate? GetTemplateById(int t) => throw new NotSupportedException();
        public IReadOnlyList<string> GetPropertyKeysForType(int t) => throw new NotSupportedException();
        public void SaveFields(int t, IEnumerable<MappingField> f, IReadOnlyDictionary<int, List<TransformerStep>> s) => throw new NotSupportedException();
        public long SaveResult(long d, MappingOutcome o) => throw new NotSupportedException();
        public (decimal? overall, bool needsReview, string? json, List<MappedValueRow> values)? GetLatestResult(long d) => throw new NotSupportedException();
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

    private static MappingController NewController(FakeMappingRepository m) => new(m, new StubDocs());

    [Fact]
    public void Rebinding_only_field_A_leaves_field_B_untouched()
    {
        var mapping = new FakeMappingRepository();
        // payload only contains field A (FieldId 1); field B (FieldId 2) is NOT sent
        var payload = new VisualSavePayload
        {
            TemplateId = 1,
            Fields =
            [
                new VisualSaveField { FieldId = 1, TargetProperty = "FieldA", DataType = "STRING",
                    SourceType = "KEY_VALUE", BindingKey = "Key A", BindingChanged = true }
            ]
        };

        NewController(mapping).VisualSave(payload);

        var u = Assert.Single(mapping.Upserts);
        Assert.Equal(1, u.FieldId);
        Assert.DoesNotContain(mapping.Upserts, x => x.FieldId == 2);  // B never written
        Assert.True(u.BindingChanged);
        Assert.NotNull(u.KeyPattern);
        Assert.Matches(new Regex(u.KeyPattern!, RegexOptions.IgnoreCase), "Key A");
    }

    [Fact]
    public void Explicit_unbind_clears_only_the_targeted_field()
    {
        var mapping = new FakeMappingRepository();
        // field A explicitly unbound: BindingChanged true, no BindingKey -> KeyPattern null
        var payload = new VisualSavePayload
        {
            TemplateId = 1,
            Fields =
            [
                new VisualSaveField { FieldId = 1, TargetProperty = "FieldA", DataType = "STRING",
                    SourceType = "KEY_VALUE", BindingKey = null, BindingChanged = true }
            ]
        };

        NewController(mapping).VisualSave(payload);

        var u = Assert.Single(mapping.Upserts);
        Assert.Equal(1, u.FieldId);
        Assert.True(u.BindingChanged);
        Assert.Null(u.KeyPattern);                                   // A's binding cleared
        Assert.DoesNotContain(mapping.Upserts, x => x.FieldId == 2); // B untouched
    }

    [Fact]
    public void Metadata_only_change_preserves_binding_columns()
    {
        var mapping = new FakeMappingRepository();
        // field touched but binding NOT changed -> server must preserve its pattern
        var payload = new VisualSavePayload
        {
            TemplateId = 1,
            Fields =
            [
                new VisualSaveField { FieldId = 1, TargetProperty = "RenamedA", DataType = "DATE",
                    SourceType = "KEY_VALUE", BindingChanged = false }
            ]
        };

        NewController(mapping).VisualSave(payload);

        var u = Assert.Single(mapping.Upserts);
        Assert.False(u.BindingChanged);   // repo will run the metadata-only UPDATE, keeping the pattern
    }
}
