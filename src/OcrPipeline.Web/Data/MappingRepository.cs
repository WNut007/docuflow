using Dapper;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;

namespace OcrPipeline.Web.Data;

public sealed class MappingRepository(SqlConnectionFactory factory)
{
    /// <summary>Loads transformer steps for every field in a template, keyed by FieldId.</summary>
    public Dictionary<int, List<TransformerStep>> GetTransformerSteps(int templateId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT s.StepId, s.FieldId, s.StepOrder, s.[Type], s.ConfigJson, s.IsActive
            FROM dbo.TransformerStep s
            JOIN dbo.MappingField f ON f.FieldId = s.FieldId
            WHERE f.TemplateId = @TemplateId AND s.IsActive = 1
            ORDER BY s.FieldId, s.StepOrder;
            """;
        return db.Query<TransformerStep>(sql, new { TemplateId = templateId })
                 .GroupBy(s => s.FieldId)
                 .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Loads active table sub-columns for every field in a template, keyed by FieldId.</summary>
    public Dictionary<int, List<MappingTableColumn>> GetTableColumns(int templateId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT c.ColumnId, c.FieldId, c.TargetSubProperty, c.DataType, c.TableHeader,
                   c.SortOrder, c.IsActive
            FROM dbo.MappingTableColumn c
            JOIN dbo.MappingField f ON f.FieldId = c.FieldId
            WHERE f.TemplateId = @TemplateId AND c.IsActive = 1
            ORDER BY c.FieldId, c.SortOrder;
            """;
        return db.Query<MappingTableColumn>(sql, new { TemplateId = templateId })
                 .GroupBy(c => c.FieldId)
                 .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Replaces a field's table sub-columns (delete-then-insert) in one transaction.</summary>
    public void SaveTableColumns(int fieldId, IEnumerable<MappingTableColumn> columns)
    {
        using var db = factory.Create();
        using var tx = db.BeginTransaction();

        db.Execute("DELETE FROM dbo.MappingTableColumn WHERE FieldId = @FieldId;", new { FieldId = fieldId }, tx);

        const string insertSql = """
            INSERT dbo.MappingTableColumn (FieldId, TargetSubProperty, DataType, TableHeader, SortOrder, IsActive)
            VALUES (@FieldId, @TargetSubProperty, @DataType, @TableHeader, @SortOrder, @IsActive);
            """;
        int order = 0;
        foreach (var c in columns)
        {
            db.Execute(insertSql, new
            {
                FieldId = fieldId,
                c.TargetSubProperty,
                c.DataType,
                c.TableHeader,
                SortOrder = c.SortOrder == 0 ? order : c.SortOrder,
                c.IsActive
            }, tx);
            order++;
        }

        tx.Commit();
    }

    public MappingTemplate? GetActiveTemplateForType(int documentTypeId)
    {
        using var db = factory.Create();

        const string tplSql = """
            SELECT TOP 1 TemplateId, DocumentTypeId, Name, TargetModel, Version, IsActive
            FROM dbo.MappingTemplate
            WHERE DocumentTypeId = @DocumentTypeId AND IsActive = 1
            ORDER BY Version DESC;
            """;
        var tpl = db.QuerySingleOrDefault<MappingTemplate>(tplSql, new { DocumentTypeId = documentTypeId });
        if (tpl is null) return null;

        const string fieldSql = """
            SELECT FieldId, TemplateId, TargetProperty, DataType, IsRequired, SourceType,
                   KeyPattern, SourcePattern, TableHeader, RowSelector, DefaultValue, MinConfidence
            FROM dbo.MappingField
            WHERE TemplateId = @TemplateId;
            """;
        tpl.Fields = db.Query<MappingField>(fieldSql, new { TemplateId = tpl.TemplateId }).ToList();
        return tpl;
    }

    // ---- Mapping UI support ------------------------------------------------

    public IReadOnlyList<(MappingTemplate tpl, string docType, int fieldCount)> GetAllTemplates()
    {
        using var db = factory.Create();
        const string sql = """
            SELECT t.TemplateId, t.DocumentTypeId, t.Name, t.TargetModel, t.Version, t.IsActive,
                   dt.DisplayName AS DocType,
                   (SELECT COUNT(*) FROM dbo.MappingField f WHERE f.TemplateId = t.TemplateId) AS FieldCount
            FROM dbo.MappingTemplate t
            JOIN dbo.DocumentType dt ON dt.DocumentTypeId = t.DocumentTypeId
            ORDER BY t.Name, t.Version DESC;
            """;
        return db.Query(sql).Select(r => (
            new MappingTemplate
            {
                TemplateId = r.TemplateId, DocumentTypeId = r.DocumentTypeId, Name = r.Name,
                TargetModel = r.TargetModel, Version = r.Version, IsActive = r.IsActive
            },
            (string)r.DocType,
            (int)r.FieldCount)).ToList();
    }

    public MappingTemplate? GetTemplateById(int templateId)
    {
        using var db = factory.Create();
        const string tplSql = """
            SELECT TemplateId, DocumentTypeId, Name, TargetModel, Version, IsActive
            FROM dbo.MappingTemplate WHERE TemplateId = @TemplateId;
            """;
        var tpl = db.QuerySingleOrDefault<MappingTemplate>(tplSql, new { TemplateId = templateId });
        if (tpl is null) return null;

        const string fieldSql = """
            SELECT FieldId, TemplateId, TargetProperty, DataType, IsRequired, SourceType,
                   KeyPattern, SourcePattern, TableHeader, RowSelector, DefaultValue, MinConfidence
            FROM dbo.MappingField WHERE TemplateId = @TemplateId ORDER BY FieldId;
            """;
        tpl.Fields = db.Query<MappingField>(fieldSql, new { TemplateId = templateId }).ToList();
        return tpl;
    }

    /// <summary>Distinct property keys discovered from documents of a type (for the source dropdown).</summary>
    public IReadOnlyList<string> GetPropertyKeysForType(int documentTypeId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT DISTINCT TOP 100 dp.[Key]
            FROM dbo.DocumentProperty dp
            JOIN dbo.Document d ON d.DocumentId = dp.DocumentId
            WHERE d.DocumentTypeId = @DocumentTypeId
            ORDER BY dp.[Key];
            """;
        return db.Query<string>(sql, new { DocumentTypeId = documentTypeId }).ToList();
    }

    /// <summary>Upserts fields and replaces their transformer steps in one transaction.</summary>
    public void SaveFields(int templateId, IEnumerable<MappingField> fields,
                           IReadOnlyDictionary<int, List<TransformerStep>> stepsByRowIndex)
    {
        using var db = factory.Create();
        using var tx = db.BeginTransaction();

        const string insertSql = """
            INSERT dbo.MappingField
                (TemplateId, TargetProperty, DataType, IsRequired, SourceType,
                 KeyPattern, SourcePattern, TableHeader, RowSelector, DefaultValue, MinConfidence)
            OUTPUT INSERTED.FieldId
            VALUES
                (@TemplateId, @TargetProperty, @DataType, @IsRequired, @SourceType,
                 @KeyPattern, @SourcePattern, @TableHeader, @RowSelector, @DefaultValue, @MinConfidence);
            """;
        const string updateSql = """
            UPDATE dbo.MappingField SET
                TargetProperty = @TargetProperty, DataType = @DataType, IsRequired = @IsRequired,
                SourceType = @SourceType, KeyPattern = @KeyPattern, SourcePattern = @SourcePattern,
                TableHeader = @TableHeader, RowSelector = @RowSelector, DefaultValue = @DefaultValue,
                MinConfidence = @MinConfidence
            WHERE FieldId = @FieldId AND TemplateId = @TemplateId;
            """;
        const string deleteStepsSql = "DELETE FROM dbo.TransformerStep WHERE FieldId = @FieldId;";
        const string insertStepSql = """
            INSERT dbo.TransformerStep (FieldId, StepOrder, [Type], ConfigJson, IsActive)
            VALUES (@FieldId, @StepOrder, @Type, @ConfigJson, 1);
            """;

        int rowIndex = 0;
        foreach (var f in fields)
        {
            f.TemplateId = templateId;
            long fieldId;
            if (f.FieldId > 0)
            {
                db.Execute(updateSql, f, tx);
                fieldId = f.FieldId;
            }
            else
            {
                fieldId = db.ExecuteScalar<long>(insertSql, f, tx);
            }

            // replace transformer steps for this field
            db.Execute(deleteStepsSql, new { FieldId = fieldId }, tx);
            if (stepsByRowIndex.TryGetValue(rowIndex, out var steps))
            {
                int order = 1;
                foreach (var s in steps)
                    db.Execute(insertStepSql, new
                    {
                        FieldId = fieldId, StepOrder = order++, s.Type, s.ConfigJson
                    }, tx);
            }
            rowIndex++;
        }

        tx.Commit();
    }

    public long SaveResult(long documentId, MappingOutcome outcome)
    {
        using var db = factory.Create();
        using var tx = db.BeginTransaction();

        const string resSql = """
            INSERT dbo.MappingResult (DocumentId, TemplateId, OverallConfidence, NeedsReview, MappedJson)
            OUTPUT INSERTED.MappingResultId
            VALUES (@DocumentId, @TemplateId, @OverallConfidence, @NeedsReview, @MappedJson);
            """;
        long resultId = db.ExecuteScalar<long>(resSql, new
        {
            DocumentId = documentId,
            outcome.TemplateId,
            outcome.OverallConfidence,
            outcome.NeedsReview,
            outcome.MappedJson
        }, tx);

        const string valSql = """
            INSERT dbo.MappingResultValue
                (MappingResultId, FieldId, TargetProperty, RawValue, NormalizedValue,
                 Confidence, SourceRef, IsBelowThreshold)
            VALUES
                (@MappingResultId, @FieldId, @TargetProperty, @RawValue, @NormalizedValue,
                 @Confidence, @SourceRef, @IsBelowThreshold);
            """;
        foreach (var v in outcome.Values)
        {
            db.Execute(valSql, new
            {
                MappingResultId = resultId,
                v.FieldId, v.TargetProperty, v.RawValue, v.NormalizedValue,
                v.Confidence, v.SourceRef, v.IsBelowThreshold
            }, tx);
        }

        tx.Commit();
        return resultId;
    }

    public (decimal? overall, bool needsReview, string? json, List<MappedValueRow> values)? GetLatestResult(long documentId)
    {
        using var db = factory.Create();
        const string resSql = """
            SELECT TOP 1 MappingResultId, OverallConfidence, NeedsReview, MappedJson
            FROM dbo.MappingResult
            WHERE DocumentId = @DocumentId
            ORDER BY MappingResultId DESC;
            """;
        var res = db.QuerySingleOrDefault(resSql, new { DocumentId = documentId });
        if (res is null) return null;

        long resultId = res.MappingResultId;
        const string valSql = """
            SELECT TargetProperty, RawValue, NormalizedValue, Confidence, SourceRef, IsBelowThreshold
            FROM dbo.MappingResultValue
            WHERE MappingResultId = @ResultId
            ORDER BY ResultValueId;
            """;
        var values = db.Query<MappedValueRow>(valSql, new { ResultId = resultId }).ToList();
        return ((decimal?)res.OverallConfidence, (bool)res.NeedsReview, (string?)res.MappedJson, values);
    }
}

public sealed class MappedValueRow
{
    public string TargetProperty { get; set; } = "";
    public string? RawValue { get; set; }
    public string? NormalizedValue { get; set; }
    public decimal? Confidence { get; set; }
    public string? SourceRef { get; set; }
    public bool IsBelowThreshold { get; set; }
}
