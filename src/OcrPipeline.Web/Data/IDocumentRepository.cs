using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Data;

/// <summary>A document candidate for the visual mapper's document selector.</summary>
public sealed record DocumentRef(long DocumentId, string FileName, int PageCount);

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
    IReadOnlyList<Document> GetByStatus(string statusCode);
    IReadOnlyList<DocumentRef> GetByTypeWithPreviews(int documentTypeId, int top = 20);
    void InsertPages(long documentId, IEnumerable<DocumentPage> pages);
    IReadOnlyList<DocumentPage> GetPages(long documentId);
    void SetClassification(long documentId, int documentTypeId, decimal confidence);
    void SetStatus(long documentId, string statusCode);
    void LogEvent(long documentId, string stage, string? fromStatus, string toStatus, string? message, int? byUserId);
}
