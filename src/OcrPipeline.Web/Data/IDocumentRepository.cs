using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Data;

/// <summary>
/// Document persistence seam. Lets services (notably PipelineService) depend on an abstraction
/// so the pipeline's status/event writes can be faked in tests without a database — while the
/// concrete <see cref="DocumentRepository"/> stays sealed.
/// </summary>
public interface IDocumentRepository
{
    long Insert(Document doc);
    Document? GetById(long documentId);
    IReadOnlyList<Document> GetRecent(int top = 50);
    void InsertPages(long documentId, IEnumerable<DocumentPage> pages);
    IReadOnlyList<DocumentPage> GetPages(long documentId);
    void SetClassification(long documentId, int documentTypeId, decimal confidence);
    void SetStatus(long documentId, string statusCode);
    void LogEvent(long documentId, string stage, string? fromStatus, string toStatus, string? message, int? byUserId);
}
