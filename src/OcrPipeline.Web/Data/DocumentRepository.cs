using Dapper;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Data;

public sealed class DocumentRepository(SqlConnectionFactory factory)
{
    public long Insert(Document doc)
    {
        using var db = factory.Create();
        const string sql = """
            INSERT dbo.Document
                (OriginalFileName, StoredPath, ContentType, FileSizeBytes, Sha256,
                 SourceChannel, StatusCode, UploadedByUserId)
            OUTPUT INSERTED.DocumentId
            VALUES
                (@OriginalFileName, @StoredPath, @ContentType, @FileSizeBytes, @Sha256,
                 @SourceChannel, @StatusCode, @UploadedByUserId);
            """;
        return db.ExecuteScalar<long>(sql, doc);
    }

    public Document? GetById(long documentId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT DocumentId, OriginalFileName, StoredPath, ContentType, FileSizeBytes,
                   Sha256, SourceChannel, DocumentTypeId, ClassifyConfidence, StatusCode,
                   PageCount, UploadedByUserId, CreatedAtUtc
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
            ORDER BY CreatedAtUtc DESC;
            """;
        return db.Query<Document>(sql, new { Top = top }).ToList();
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
