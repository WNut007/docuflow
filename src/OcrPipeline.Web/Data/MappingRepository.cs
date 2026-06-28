using Dapper;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;

namespace OcrPipeline.Web.Data;

public sealed class MappingRepository(SqlConnectionFactory factory) : IMappingRepository
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
                   c.SortOrder, c.IsActive,
                   c.ColXStart, c.ColXEnd, c.IsAnchor, c.LineSelectMode, c.LineSelectIndices, c.LineJoinSeparator,
                   c.LineOffset
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
            INSERT dbo.MappingTableColumn
                (FieldId, TargetSubProperty, DataType, TableHeader, SortOrder, IsActive,
                 ColXStart, ColXEnd, IsAnchor, LineSelectMode, LineSelectIndices, LineJoinSeparator, LineOffset)
            VALUES
                (@FieldId, @TargetSubProperty, @DataType, @TableHeader, @SortOrder, @IsActive,
                 @ColXStart, @ColXEnd, @IsAnchor, @LineSelectMode, @LineSelectIndices, @LineJoinSeparator, @LineOffset);
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
                c.IsActive,
                c.ColXStart, c.ColXEnd, c.IsAnchor, c.LineSelectMode, c.LineSelectIndices, c.LineJoinSeparator,
                c.LineOffset
            }, tx);
            order++;
        }

        tx.Commit();
    }

    public IReadOnlyList<TemplateResolver.Candidate> GetTemplatesForType(int documentTypeId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT t.TemplateId, t.DocumentTypeId, t.Version, t.IsActive,
                   CAST(CASE WHEN EXISTS (
                       SELECT 1 FROM dbo.MappingField f
                       WHERE f.TemplateId = t.TemplateId AND f.ZonePageRole IS NOT NULL
                   ) THEN 1 ELSE 0 END AS BIT) AS IsMultiPage
            FROM dbo.MappingTemplate t
            WHERE t.DocumentTypeId = @DocumentTypeId;
            """;
        return db.Query<TemplateResolver.Candidate>(sql, new { DocumentTypeId = documentTypeId }).ToList();
    }

    public int CreateTemplate(int documentTypeId, string name, string targetModel, string mappingMode)
    {
        using var db = factory.Create();
        const string sql = """
            INSERT dbo.MappingTemplate (DocumentTypeId, Name, TargetModel, Version, IsActive, MappingMode)
            OUTPUT INSERTED.TemplateId
            VALUES (@DocumentTypeId, @Name, @TargetModel, 1, 1, @MappingMode);
            """;
        return db.ExecuteScalar<int>(sql, new
        {
            DocumentTypeId = documentTypeId, Name = name,
            TargetModel = targetModel, MappingMode = mappingMode
        });
    }

    public void SetTemplateSample(int templateId, long documentId)
    {
        using var db = factory.Create();
        const string sql = "UPDATE dbo.MappingTemplate SET SampleDocumentId = @DocumentId WHERE TemplateId = @TemplateId;";
        db.Execute(sql, new { TemplateId = templateId, DocumentId = documentId });
    }

    public MappingTemplate? GetActiveTemplateForType(int documentTypeId)
    {
        using var db = factory.Create();

        const string tplSql = """
            SELECT TOP 1 TemplateId, DocumentTypeId, Name, TargetModel, Version, IsActive, MappingMode
            FROM dbo.MappingTemplate
            WHERE DocumentTypeId = @DocumentTypeId AND IsActive = 1
            ORDER BY Version DESC;
            """;
        var tpl = db.QuerySingleOrDefault<MappingTemplate>(tplSql, new { DocumentTypeId = documentTypeId });
        if (tpl is null) return null;

        const string fieldSql = """
            SELECT FieldId, TemplateId, TargetProperty, DataType, IsRequired, SourceType,
                   KeyPattern, SourcePattern, TableHeader, RowSelector, DefaultValue, MinConfidence,
                   ZonePage, ZoneX, ZoneY, ZoneW, ZoneH, ZoneOcrHint, ZonePsm, ZonePageRole
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

    public IReadOnlyList<(int Id, string Name)> GetDocumentTypes()
    {
        using var db = factory.Create();
        const string sql = """
            SELECT DocumentTypeId, DisplayName
            FROM dbo.DocumentType
            WHERE IsActive = 1
            ORDER BY DocumentTypeId;
            """;
        return db.Query(sql).Select(r => ((int)r.DocumentTypeId, (string)r.DisplayName)).ToList();
    }

    public MappingTemplate? GetTemplateById(int templateId)
    {
        using var db = factory.Create();
        const string tplSql = """
            SELECT TemplateId, DocumentTypeId, Name, TargetModel, Version, IsActive, MappingMode, SampleDocumentId
            FROM dbo.MappingTemplate WHERE TemplateId = @TemplateId;
            """;
        var tpl = db.QuerySingleOrDefault<MappingTemplate>(tplSql, new { TemplateId = templateId });
        if (tpl is null) return null;

        const string fieldSql = """
            SELECT FieldId, TemplateId, TargetProperty, DataType, IsRequired, SourceType,
                   KeyPattern, SourcePattern, TableHeader, RowSelector, DefaultValue, MinConfidence,
                   ZonePage, ZoneX, ZoneY, ZoneW, ZoneH, ZoneOcrHint, ZonePsm, ZonePageRole
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

    /// <summary>
    /// Saves the zone designer: sets the template MappingMode, then upserts each field's zone
    /// rectangle + OCR hint in one transaction (insert when FieldId == 0). Parameterized throughout.
    /// </summary>
    public void SaveZones(int templateId, string mappingMode, IEnumerable<MappingField> fields)
    {
        using var db = factory.Create();
        using var tx = db.BeginTransaction();

        db.Execute(
            "UPDATE dbo.MappingTemplate SET MappingMode = @MappingMode WHERE TemplateId = @TemplateId;",
            new { TemplateId = templateId, MappingMode = mappingMode }, tx);

        const string insertSql = """
            INSERT dbo.MappingField
                (TemplateId, TargetProperty, DataType, IsRequired, SourceType, MinConfidence,
                 ZonePage, ZoneX, ZoneY, ZoneW, ZoneH, ZoneOcrHint, ZonePsm, ZonePageRole)
            VALUES
                (@TemplateId, @TargetProperty, @DataType, @IsRequired, @SourceType, @MinConfidence,
                 @ZonePage, @ZoneX, @ZoneY, @ZoneW, @ZoneH, @ZoneOcrHint, @ZonePsm, @ZonePageRole);
            """;
        const string updateSql = """
            UPDATE dbo.MappingField SET
                TargetProperty = @TargetProperty, DataType = @DataType, IsRequired = @IsRequired,
                MinConfidence = @MinConfidence,
                ZonePage = @ZonePage, ZoneX = @ZoneX, ZoneY = @ZoneY, ZoneW = @ZoneW, ZoneH = @ZoneH,
                ZoneOcrHint = @ZoneOcrHint, ZonePsm = @ZonePsm, ZonePageRole = @ZonePageRole
            WHERE FieldId = @FieldId AND TemplateId = @TemplateId;
            """;

        foreach (var f in fields)
        {
            f.TemplateId = templateId;
            if (string.IsNullOrWhiteSpace(f.SourceType)) f.SourceType = "KEY_VALUE";
            db.Execute(f.FieldId > 0 ? updateSql : insertSql, f, tx);
        }

        tx.Commit();
    }

    /// <summary>
    /// Upserts a line_item TABLE_CELL field (its zone rect) and replaces its sub-columns in one
    /// transaction. Insert returns the new FieldId so the columns can be attached. Parameterized.
    /// </summary>
    public int SaveTableZone(int templateId, MappingField field, IEnumerable<MappingTableColumn> columns)
    {
        using var db = factory.Create();
        using var tx = db.BeginTransaction();

        field.TemplateId = templateId;
        field.SourceType = "TABLE_CELL";

        const string insertSql = """
            INSERT dbo.MappingField
                (TemplateId, TargetProperty, DataType, IsRequired, SourceType, MinConfidence,
                 ZonePage, ZoneX, ZoneY, ZoneW, ZoneH, ZoneOcrHint, ZonePsm, ZonePageRole)
            OUTPUT INSERTED.FieldId
            VALUES
                (@TemplateId, @TargetProperty, @DataType, @IsRequired, @SourceType, @MinConfidence,
                 @ZonePage, @ZoneX, @ZoneY, @ZoneW, @ZoneH, @ZoneOcrHint, @ZonePsm, @ZonePageRole);
            """;
        const string updateSql = """
            UPDATE dbo.MappingField SET
                TargetProperty = @TargetProperty, DataType = @DataType, IsRequired = @IsRequired,
                SourceType = @SourceType, MinConfidence = @MinConfidence,
                ZonePage = @ZonePage, ZoneX = @ZoneX, ZoneY = @ZoneY, ZoneW = @ZoneW, ZoneH = @ZoneH,
                ZoneOcrHint = @ZoneOcrHint, ZonePsm = @ZonePsm, ZonePageRole = @ZonePageRole
            WHERE FieldId = @FieldId AND TemplateId = @TemplateId;
            """;

        int fieldId = field.FieldId;
        if (fieldId > 0) db.Execute(updateSql, field, tx);
        else fieldId = db.ExecuteScalar<int>(insertSql, field, tx);

        // replace the field's sub-columns (delete-then-insert) in the SAME transaction
        db.Execute("DELETE FROM dbo.MappingTableColumn WHERE FieldId = @FieldId;", new { FieldId = fieldId }, tx);
        const string colInsert = """
            INSERT dbo.MappingTableColumn
                (FieldId, TargetSubProperty, DataType, TableHeader, SortOrder, IsActive,
                 ColXStart, ColXEnd, IsAnchor, LineSelectMode, LineSelectIndices, LineJoinSeparator, LineOffset)
            VALUES
                (@FieldId, @TargetSubProperty, @DataType, @TableHeader, @SortOrder, @IsActive,
                 @ColXStart, @ColXEnd, @IsAnchor, @LineSelectMode, @LineSelectIndices, @LineJoinSeparator, @LineOffset);
            """;
        int order = 0;
        foreach (var c in columns)
        {
            db.Execute(colInsert, new
            {
                FieldId = fieldId,
                c.TargetSubProperty,
                c.DataType,
                c.TableHeader,
                SortOrder = c.SortOrder == 0 ? order : c.SortOrder,
                c.IsActive,
                c.ColXStart, c.ColXEnd, c.IsAnchor, c.LineSelectMode, c.LineSelectIndices, c.LineJoinSeparator,
                c.LineOffset
            }, tx);
            order++;
        }

        tx.Commit();
        return fieldId;
    }

    public int DeleteZoneFields(int templateId, IEnumerable<int> fieldIds)
    {
        var ids = fieldIds.Where(i => i > 0).Distinct().ToList();
        if (ids.Count == 0) return 0;

        using var db = factory.Create();
        using var tx = db.BeginTransaction();
        int deleted = 0;
        foreach (var id in ids)
        {
            // FK-safe: a region still referenced by a stored result (FK_MRV_Field) is left in place
            // rather than cascade-deleting extraction history. (A redundant region never emits a
            // result value, so the one the user removes is normally unreferenced.)
            bool referenced = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.MappingResultValue WHERE FieldId = @id;", new { id }, tx) > 0;
            if (referenced) continue;

            db.Execute("DELETE FROM dbo.MappingTableColumn WHERE FieldId = @id;", new { id }, tx);
            deleted += db.Execute(
                "DELETE FROM dbo.MappingField WHERE FieldId = @id AND TemplateId = @templateId;",
                new { id, templateId }, tx);
        }
        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// Partial upsert used by the visual mapper. Only the given field is written; fields not passed
    /// in are untouched, and transformer steps are never modified (unlike SaveFields). When
    /// bindingChanged is false, the binding columns are preserved (only metadata is updated).
    /// </summary>
    public int UpsertFieldBinding(int templateId, MappingField f, bool bindingChanged)
    {
        using var db = factory.Create();

        if (f.FieldId <= 0)
        {
            const string insertSql = """
                INSERT dbo.MappingField
                    (TemplateId, TargetProperty, DataType, IsRequired, SourceType,
                     KeyPattern, SourcePattern, TableHeader, RowSelector, DefaultValue, MinConfidence)
                OUTPUT INSERTED.FieldId
                VALUES
                    (@TemplateId, @TargetProperty, @DataType, @IsRequired, @SourceType,
                     @KeyPattern, @SourcePattern, @TableHeader, @RowSelector, @DefaultValue, @MinConfidence);
                """;
            f.TemplateId = templateId;
            return (int)db.ExecuteScalar<long>(insertSql, f);
        }

        if (bindingChanged)
        {
            const string fullSql = """
                UPDATE dbo.MappingField SET
                    TargetProperty = @TargetProperty, DataType = @DataType, IsRequired = @IsRequired,
                    SourceType = @SourceType, KeyPattern = @KeyPattern, SourcePattern = @SourcePattern,
                    TableHeader = @TableHeader, RowSelector = @RowSelector, DefaultValue = @DefaultValue,
                    MinConfidence = @MinConfidence
                WHERE FieldId = @FieldId AND TemplateId = @TemplateId;
                """;
            f.TemplateId = templateId;
            db.Execute(fullSql, f);
        }
        else
        {
            // metadata only — preserve the existing KeyPattern/SourcePattern/TableHeader/RowSelector
            const string metaSql = """
                UPDATE dbo.MappingField SET
                    TargetProperty = @TargetProperty, DataType = @DataType,
                    IsRequired = @IsRequired, MinConfidence = @MinConfidence
                WHERE FieldId = @FieldId AND TemplateId = @TemplateId;
                """;
            db.Execute(metaSql, new
            {
                f.FieldId, TemplateId = templateId, f.TargetProperty, f.DataType, f.IsRequired, f.MinConfidence
            });
        }
        return f.FieldId;
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

    public (decimal? overall, bool needsReview, string? json, int templateId, List<MappedValueRow> values)? GetLatestResult(long documentId)
    {
        using var db = factory.Create();
        const string resSql = """
            SELECT TOP 1 MappingResultId, OverallConfidence, NeedsReview, MappedJson, TemplateId
            FROM dbo.MappingResult
            WHERE DocumentId = @DocumentId
            ORDER BY MappingResultId DESC;
            """;
        var res = db.QuerySingleOrDefault(resSql, new { DocumentId = documentId });
        if (res is null) return null;

        long resultId = res.MappingResultId;
        const string valSql = """
            SELECT ResultValueId, FieldId, TargetProperty, RawValue, NormalizedValue,
                   Confidence, SourceRef, IsBelowThreshold
            FROM dbo.MappingResultValue
            WHERE MappingResultId = @ResultId
            ORDER BY ResultValueId;
            """;
        var values = db.Query<MappedValueRow>(valSql, new { ResultId = resultId }).ToList();
        return ((decimal?)res.OverallConfidence, (bool)res.NeedsReview, (string?)res.MappedJson, (int)res.TemplateId, values);
    }

    /// <summary>
    /// Applies a reviewer's correction to one value: sets NormalizedValue and clears the
    /// below-threshold flag. Scoped through MappingResult so the row must belong to the document
    /// (prevents updating another document's values via a forged id). Returns rows affected.
    /// </summary>
    public int UpdateResultValue(long documentId, long resultValueId, string? normalizedValue)
    {
        using var db = factory.Create();
        const string sql = """
            UPDATE v
            SET v.NormalizedValue = @NormalizedValue,
                v.IsBelowThreshold = 0
            FROM dbo.MappingResultValue v
            JOIN dbo.MappingResult r ON r.MappingResultId = v.MappingResultId
            WHERE v.ResultValueId = @ResultValueId AND r.DocumentId = @DocumentId;
            """;
        return db.Execute(sql, new { DocumentId = documentId, ResultValueId = resultValueId, NormalizedValue = normalizedValue });
    }
}

public sealed class MappedValueRow
{
    public long ResultValueId { get; set; }
    public int FieldId { get; set; }
    public string TargetProperty { get; set; } = "";
    public string? RawValue { get; set; }
    public string? NormalizedValue { get; set; }
    public decimal? Confidence { get; set; }
    public string? SourceRef { get; set; }
    public bool IsBelowThreshold { get; set; }
}
