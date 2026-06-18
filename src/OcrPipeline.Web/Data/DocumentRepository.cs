using Dapper;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Data;

public sealed class DocumentRepository(SqlConnectionFactory factory) : IDocumentRepository
{
    public long Insert(Document doc)
    {
        using var db = factory.Create();
        const string sql = """
            INSERT dbo.Document
                (OriginalFileName, StoredPath, ContentType, FileSizeBytes, Sha256,
                 SourceChannel, StatusCode, UploadedByUserId, OcrLanguages, TemplateId)
            OUTPUT INSERTED.DocumentId
            VALUES
                (@OriginalFileName, @StoredPath, @ContentType, @FileSizeBytes, @Sha256,
                 @SourceChannel, @StatusCode, @UploadedByUserId, @OcrLanguages, @TemplateId);
            """;
        return db.ExecuteScalar<long>(sql, doc);
    }

    public Document? GetById(long documentId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT DocumentId, OriginalFileName, StoredPath, ContentType, FileSizeBytes,
                   Sha256, SourceChannel, DocumentTypeId, TemplateId, ClassifyConfidence, StatusCode,
                   PageCount, UploadedByUserId, OcrLanguages, CreatedAtUtc
            FROM dbo.Document
            WHERE DocumentId = @DocumentId;
            """;
        return db.QuerySingleOrDefault<Document>(sql, new { DocumentId = documentId });
    }

    public IReadOnlyList<Document> GetRecent(int top = 50)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT TOP (@Top)
                   DocumentId, OriginalFileName, ContentType, FileSizeBytes, SourceChannel,
                   DocumentTypeId, StatusCode, PageCount, CreatedAtUtc, StoredPath, Sha256,
                   ClassifyConfidence, UploadedByUserId
            FROM dbo.Document
            WHERE StatusCode <> 'SAMPLE'   -- template-designer backdrops are not real documents
            ORDER BY CreatedAtUtc DESC;
            """;
        return db.Query<Document>(sql, new { Top = top }).ToList();
    }

    /// <summary>All documents in a given status (used on startup to re-enqueue CAPTURED docs).</summary>
    public IReadOnlyList<Document> GetByStatus(string statusCode)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT DocumentId, OriginalFileName, StoredPath, ContentType, FileSizeBytes,
                   Sha256, SourceChannel, DocumentTypeId, TemplateId, ClassifyConfidence, StatusCode,
                   PageCount, UploadedByUserId, OcrLanguages, CreatedAtUtc
            FROM dbo.Document
            WHERE StatusCode = @StatusCode
            ORDER BY DocumentId;
            """;
        return db.Query<Document>(sql, new { StatusCode = statusCode }).ToList();
    }

    /// <summary>Documents of a type that already have rendered page previews (for the visual mapper).</summary>
    public IReadOnlyList<DocumentRef> GetByTypeWithPreviews(int documentTypeId, int top = 20)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT TOP (@Top) d.DocumentId, d.OriginalFileName AS FileName, d.PageCount
            FROM dbo.Document d
            WHERE d.DocumentTypeId = @DocumentTypeId
              AND d.StatusCode <> 'SAMPLE'   -- exclude template-designer backdrops
              AND EXISTS (SELECT 1 FROM dbo.DocumentPage p WHERE p.DocumentId = d.DocumentId)
            ORDER BY d.CreatedAtUtc DESC;
            """;
        return db.Query<DocumentRef>(sql, new { DocumentTypeId = documentTypeId, Top = top }).ToList();
    }

    /// <summary>Persists per-page pixel dimensions and updates the document's page count.</summary>
    public void InsertPages(long documentId, IEnumerable<DocumentPage> pages)
    {
        using var db = factory.Create();
        const string sql = """
            INSERT dbo.DocumentPage (DocumentId, PageNumber, WidthPx, HeightPx)
            VALUES (@DocumentId, @PageNumber, @WidthPx, @HeightPx);
            """;
        int count = 0;
        foreach (var p in pages)
        {
            db.Execute(sql, new { DocumentId = documentId, p.PageNumber, p.WidthPx, p.HeightPx });
            count++;
        }

        db.Execute(
            "UPDATE dbo.Document SET PageCount = @PageCount, UpdatedAtUtc = SYSUTCDATETIME() WHERE DocumentId = @DocumentId;",
            new { DocumentId = documentId, PageCount = count });
    }

    public IReadOnlyList<DocumentPage> GetPages(long documentId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT PageId, DocumentId, PageNumber, WidthPx, HeightPx
            FROM dbo.DocumentPage
            WHERE DocumentId = @DocumentId
            ORDER BY PageNumber;
            """;
        return db.Query<DocumentPage>(sql, new { DocumentId = documentId }).ToList();
    }

    public void SetClassification(long documentId, int documentTypeId, decimal confidence)
    {
        using var db = factory.Create();
        const string sql = """
            UPDATE dbo.Document
            SET DocumentTypeId = @DocumentTypeId,
                ClassifyConfidence = @Confidence,
                StatusCode = 'CLASSIFIED',
                UpdatedAtUtc = SYSUTCDATETIME()
            WHERE DocumentId = @DocumentId;
            """;
        db.Execute(sql, new { DocumentId = documentId, DocumentTypeId = documentTypeId, Confidence = confidence });
    }

    public void SetStatus(long documentId, string statusCode)
    {
        using var db = factory.Create();
        const string sql = """
            UPDATE dbo.Document
            SET StatusCode = @StatusCode, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE DocumentId = @DocumentId;
            """;
        db.Execute(sql, new { DocumentId = documentId, StatusCode = statusCode });
    }

    public void LogEvent(long documentId, string stage, string? fromStatus, string toStatus, string? message, int? byUserId)
    {
        using var db = factory.Create();
        const string sql = """
            INSERT dbo.PipelineEvent (DocumentId, Stage, FromStatus, ToStatus, Message, ByUserId)
            VALUES (@DocumentId, @Stage, @FromStatus, @ToStatus, @Message, @ByUserId);
            """;
        db.Execute(sql, new { DocumentId = documentId, Stage = stage, FromStatus = fromStatus, ToStatus = toStatus, Message = message, ByUserId = byUserId });
    }
}
