using Dapper;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Data;

public interface IExportRepository
{
    IReadOnlyList<ExportTarget> GetAllTargets();
    IReadOnlyList<ExportTarget> GetActiveTargets(int? documentTypeId);
    ExportTarget? GetTargetById(int targetId);
    long InsertLog(ExportLog log);
    IReadOnlyList<ExportLog> GetLogsForDocument(long documentId);
    IReadOnlyList<ExportLog> GetRecentLogs(int top = 50);
}

public sealed class ExportRepository(SqlConnectionFactory factory) : IExportRepository
{
    private const string TargetCols =
        "TargetId, Name, Kind, Endpoint, AuthHeaderName, AuthSecret, DocumentTypeId, IsActive";

    public IReadOnlyList<ExportTarget> GetAllTargets()
    {
        using var db = factory.Create();
        return db.Query<ExportTarget>($"SELECT {TargetCols} FROM dbo.ExportTarget ORDER BY Name;").ToList();
    }

    /// <summary>Active targets that apply to this document type (or to all types when DocumentTypeId is NULL).</summary>
    public IReadOnlyList<ExportTarget> GetActiveTargets(int? documentTypeId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT TargetId, Name, Kind, Endpoint, AuthHeaderName, AuthSecret, DocumentTypeId, IsActive
            FROM dbo.ExportTarget
            WHERE IsActive = 1 AND (DocumentTypeId IS NULL OR DocumentTypeId = @DocumentTypeId)
            ORDER BY TargetId;
            """;
        return db.Query<ExportTarget>(sql, new { DocumentTypeId = documentTypeId }).ToList();
    }

    public ExportTarget? GetTargetById(int targetId)
    {
        using var db = factory.Create();
        return db.QuerySingleOrDefault<ExportTarget>(
            $"SELECT {TargetCols} FROM dbo.ExportTarget WHERE TargetId = @TargetId;", new { TargetId = targetId });
    }

    public long InsertLog(ExportLog log)
    {
        using var db = factory.Create();
        const string sql = """
            INSERT dbo.ExportLog (DocumentId, TargetId, StatusCode, HttpStatus, ResponseSnippet, Attempt)
            OUTPUT INSERTED.LogId
            VALUES (@DocumentId, @TargetId, @StatusCode, @HttpStatus, @ResponseSnippet, @Attempt);
            """;
        return db.ExecuteScalar<long>(sql, log);
    }

    public IReadOnlyList<ExportLog> GetLogsForDocument(long documentId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT l.LogId, l.DocumentId, l.TargetId, l.StatusCode, l.HttpStatus, l.ResponseSnippet,
                   l.Attempt, l.CreatedAtUtc, t.Name AS TargetName
            FROM dbo.ExportLog l
            LEFT JOIN dbo.ExportTarget t ON t.TargetId = l.TargetId
            WHERE l.DocumentId = @DocumentId
            ORDER BY l.LogId DESC;
            """;
        return db.Query<ExportLog>(sql, new { DocumentId = documentId }).ToList();
    }

    public IReadOnlyList<ExportLog> GetRecentLogs(int top = 50)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT TOP (@Top) l.LogId, l.DocumentId, l.TargetId, l.StatusCode, l.HttpStatus,
                   l.ResponseSnippet, l.Attempt, l.CreatedAtUtc, t.Name AS TargetName
            FROM dbo.ExportLog l
            LEFT JOIN dbo.ExportTarget t ON t.TargetId = l.TargetId
            ORDER BY l.LogId DESC;
            """;
        return db.Query<ExportLog>(sql, new { Top = top }).ToList();
    }
}
