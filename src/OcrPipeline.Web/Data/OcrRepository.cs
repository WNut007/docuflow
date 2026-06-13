using Dapper;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Data;

public sealed class OcrRepository(SqlConnectionFactory factory)
{
    /// <summary>Persists a full OCR extraction (run + text blocks + tables + cells).</summary>
    public long SaveExtraction(long documentId, OcrExtraction ex)
    {
        using var db = factory.Create();
        using var tx = db.BeginTransaction();

        const string runSql = """
            INSERT dbo.OcrRun (DocumentId, Engine, EngineVersion, FinishedAtUtc, Succeeded, RawJson)
            OUTPUT INSERTED.OcrRunId
            VALUES (@DocumentId, @Engine, @EngineVersion, SYSUTCDATETIME(), 1, @RawJson);
            """;
        long runId = db.ExecuteScalar<long>(runSql,
            new { DocumentId = documentId, ex.Engine, ex.EngineVersion, ex.RawJson }, tx);

        const string blockSql = """
            INSERT dbo.OcrTextBlock
                (OcrRunId, PageNumber, BlockType, Content, Confidence,
                 BBoxLeft, BBoxTop, BBoxWidth, BBoxHeight)
            VALUES
                (@OcrRunId, @PageNumber, @BlockType, @Content, @Confidence,
                 @BBoxLeft, @BBoxTop, @BBoxWidth, @BBoxHeight);
            """;
        foreach (var b in ex.TextBlocks)
        {
            db.Execute(blockSql, new
            {
                OcrRunId = runId, b.PageNumber, b.BlockType, b.Content, b.Confidence,
                b.BBoxLeft, b.BBoxTop, b.BBoxWidth, b.BBoxHeight
            }, tx);
        }

        const string tableSql = """
            INSERT dbo.OcrTable (OcrRunId, PageNumber, TableIndex, [RowCount], ColumnCount, Confidence)
            OUTPUT INSERTED.OcrTableId
            VALUES (@OcrRunId, @PageNumber, @TableIndex, @RowCount, @ColumnCount, @Confidence);
            """;
        const string cellSql = """
            INSERT dbo.OcrTableCell
                (OcrTableId, RowIndex, ColIndex, RowSpan, ColSpan, IsHeader, Content, Confidence)
            VALUES
                (@OcrTableId, @RowIndex, @ColIndex, @RowSpan, @ColSpan, @IsHeader, @Content, @Confidence);
            """;
        foreach (var t in ex.Tables)
        {
            long tableId = db.ExecuteScalar<long>(tableSql, new
            {
                OcrRunId = runId, t.PageNumber, t.TableIndex, t.RowCount, t.ColumnCount, t.Confidence
            }, tx);

            foreach (var c in t.Cells)
            {
                db.Execute(cellSql, new
                {
                    OcrTableId = tableId, c.RowIndex, c.ColIndex, c.RowSpan, c.ColSpan,
                    c.IsHeader, c.Content, c.Confidence
                }, tx);
            }
        }

        tx.Commit();
        return runId;
    }

    public OcrExtraction? LoadLatest(long documentId)
    {
        using var db = factory.Create();

        const string runSql = """
            SELECT TOP 1 OcrRunId, Engine, EngineVersion
            FROM dbo.OcrRun
            WHERE DocumentId = @DocumentId AND Succeeded = 1
            ORDER BY OcrRunId DESC;
            """;
        var run = db.QuerySingleOrDefault(runSql, new { DocumentId = documentId });
        if (run is null) return null;

        long runId = run.OcrRunId;
        var ex = new OcrExtraction { Engine = run.Engine, EngineVersion = run.EngineVersion };

        ex.TextBlocks = db.Query<OcrTextBlock>(
            "SELECT * FROM dbo.OcrTextBlock WHERE OcrRunId = @RunId ORDER BY PageNumber, TextBlockId;",
            new { RunId = runId }).ToList();

        var tables = db.Query<OcrTable>(
            "SELECT * FROM dbo.OcrTable WHERE OcrRunId = @RunId ORDER BY PageNumber, TableIndex;",
            new { RunId = runId }).ToList();

        foreach (var t in tables)
        {
            t.Cells = db.Query<OcrTableCell>(
                "SELECT * FROM dbo.OcrTableCell WHERE OcrTableId = @TableId ORDER BY RowIndex, ColIndex;",
                new { TableId = t.OcrTableId }).ToList();
        }
        ex.Tables = tables;
        return ex;
    }

    /// <summary>Derives flat key/value properties from KEY/VALUE text blocks and saves them.</summary>
    public void SaveProperties(long documentId, long ocrRunId, OcrExtraction ex)
    {
        using var db = factory.Create();
        const string sql = """
            INSERT dbo.DocumentProperty (DocumentId, OcrRunId, [Key], [Value], Confidence, SourceRef)
            VALUES (@DocumentId, @OcrRunId, @Key, @Value, @Confidence, @SourceRef);
            """;
        foreach (var b in ex.TextBlocks)
        {
            var idx = b.Content.IndexOf(':');
            if (idx <= 0) continue;
            db.Execute(sql, new
            {
                DocumentId = documentId,
                OcrRunId = ocrRunId,
                Key = b.Content[..idx].Trim(),
                Value = b.Content[(idx + 1)..].Trim(),
                Confidence = b.Confidence,
                SourceRef = $"TextBlock:{b.TextBlockId}"
            });
        }
    }

    public IReadOnlyList<DocumentProperty> LoadProperties(long documentId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT PropertyId, DocumentId, OcrRunId, [Key], [Value], Confidence, SourceRef
            FROM dbo.DocumentProperty
            WHERE DocumentId = @DocumentId
            ORDER BY PropertyId;
            """;
        return db.Query<DocumentProperty>(sql, new { DocumentId = documentId }).ToList();
    }
}
