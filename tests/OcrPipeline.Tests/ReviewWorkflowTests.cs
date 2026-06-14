using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Mapping;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline test (no DB): saving a correction on a NEEDS_REVIEW document moves it to VALIDATED and
/// logs a VALIDATE PipelineEvent — exercised through the IDocumentRepository fake. A MAPPED document
/// is left unchanged.
/// </summary>
public sealed class ReviewWorkflowTests
{
    private sealed class FakeDocumentRepository : IDocumentRepository
    {
        public string? Status { get; private set; }
        public List<(string Stage, string? From, string To, string? Message)> Events { get; } = [];

        public void SetStatus(long id, string statusCode) => Status = statusCode;
        public void LogEvent(long id, string stage, string? from, string to, string? message, int? byUserId)
            => Events.Add((stage, from, to, message));

        // unused on this path
        public Document? GetById(long id) => throw new NotSupportedException();
        public long Insert(Document d) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetRecent(int top = 50) => throw new NotSupportedException();
        public IReadOnlyList<DocumentRef> GetByTypeWithPreviews(int documentTypeId, int top = 20) => throw new NotSupportedException();
        public void InsertPages(long id, IEnumerable<DocumentPage> pages) => throw new NotSupportedException();
        public IReadOnlyList<DocumentPage> GetPages(long id) => throw new NotSupportedException();
        public void SetClassification(long id, int typeId, decimal conf) => throw new NotSupportedException();
    }

    [Fact]
    public void NeedsReview_to_Validated_logs_event()
    {
        var docs = new FakeDocumentRepository();
        var doc = new Document { DocumentId = 5, StatusCode = "NEEDS_REVIEW" };

        var status = ReviewWorkflow.Finalize(docs, doc, corrections: 2, byUserId: 7);

        Assert.Equal("VALIDATED", status);
        Assert.Equal("VALIDATED", docs.Status);
        var ev = Assert.Single(docs.Events);
        Assert.Equal("VALIDATE", ev.Stage);
        Assert.Equal("NEEDS_REVIEW", ev.From);
        Assert.Equal("VALIDATED", ev.To);
    }

    [Fact]
    public void Mapped_document_is_left_unchanged()
    {
        var docs = new FakeDocumentRepository();
        var doc = new Document { DocumentId = 6, StatusCode = "MAPPED" };

        var status = ReviewWorkflow.Finalize(docs, doc, corrections: 1, byUserId: null);

        Assert.Equal("MAPPED", status);
        Assert.Null(docs.Status);          // SetStatus never called
        Assert.Empty(docs.Events);         // no VALIDATE event
    }
}
