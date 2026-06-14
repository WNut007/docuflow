using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;

namespace OcrPipeline.Web.Data;

/// <summary>
/// Mapping persistence seam. Lets controllers depend on an abstraction so the visual mapper's
/// partial-upsert behaviour (only touched fields are written) can be faked in tests without a DB.
/// </summary>
public interface IMappingRepository
{
    Dictionary<int, List<TransformerStep>> GetTransformerSteps(int templateId);
    Dictionary<int, List<MappingTableColumn>> GetTableColumns(int templateId);
    void SaveTableColumns(int fieldId, IEnumerable<MappingTableColumn> columns);
    MappingTemplate? GetActiveTemplateForType(int documentTypeId);
    IReadOnlyList<(MappingTemplate tpl, string docType, int fieldCount)> GetAllTemplates();
    MappingTemplate? GetTemplateById(int templateId);
    IReadOnlyList<string> GetPropertyKeysForType(int documentTypeId);
    void SaveFields(int templateId, IEnumerable<MappingField> fields,
        IReadOnlyDictionary<int, List<TransformerStep>> stepsByRowIndex);

    /// <summary>
    /// Partial upsert for the visual mapper. New field (FieldId == 0) -> insert. Existing field:
    /// when <paramref name="bindingChanged"/> is true the binding columns are rewritten; otherwise
    /// only metadata (name/type/required/min-confidence) is updated and the existing
    /// KeyPattern/SourcePattern/TableHeader/RowSelector are preserved. Never touches transformer steps.
    /// Returns the field id.
    /// </summary>
    int UpsertFieldBinding(int templateId, MappingField field, bool bindingChanged);

    long SaveResult(long documentId, MappingOutcome outcome);
    (decimal? overall, bool needsReview, string? json, List<MappedValueRow> values)? GetLatestResult(long documentId);
    int UpdateResultValue(long documentId, long resultValueId, string? normalizedValue);
}
