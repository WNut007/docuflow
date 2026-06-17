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

    /// <summary>All templates of a type as selection candidates (incl. whether each is multi-page),
    /// for <see cref="OcrPipeline.Web.Services.Mapping.TemplateResolver"/>.</summary>
    IReadOnlyList<TemplateResolver.Candidate> GetTemplatesForType(int documentTypeId);

    /// <summary>Creates an empty template for a document type; returns the new TemplateId.</summary>
    int CreateTemplate(int documentTypeId, string name, string targetModel, string mappingMode);

    IReadOnlyList<(MappingTemplate tpl, string docType, int fieldCount)> GetAllTemplates();

    /// <summary>Active document types (Id + display name) for the New-template picker.</summary>
    IReadOnlyList<(int Id, string Name)> GetDocumentTypes();

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

    /// <summary>
    /// Saves the zone designer: sets the template's MappingMode and upserts each field's zone
    /// rectangle + OCR hint (insert when FieldId == 0). Used by the zonal (template-based) mapper.
    /// </summary>
    void SaveZones(int templateId, string mappingMode, IEnumerable<MappingField> fields);

    /// <summary>
    /// Upserts a single line_item TABLE_CELL field (its zone rect) AND replaces its sub-columns
    /// (x-boundaries / anchor / line rule) in one transaction. Returns the field id (new on insert).
    /// </summary>
    int SaveTableZone(int templateId, MappingField tableField, IEnumerable<MappingTableColumn> columns);

    /// <summary>
    /// Deletes designer fields the user removed (and their sub-columns), within the template. FK-safe:
    /// a field still referenced by a stored extraction result (FK_MRV_Field) is skipped, never
    /// cascaded — history is preserved. Returns the number actually deleted.
    /// </summary>
    int DeleteZoneFields(int templateId, IEnumerable<int> fieldIds);

    long SaveResult(long documentId, MappingOutcome outcome);
    (decimal? overall, bool needsReview, string? json, int templateId, List<MappedValueRow> values)? GetLatestResult(long documentId);
    int UpdateResultValue(long documentId, long resultValueId, string? normalizedValue);
}
