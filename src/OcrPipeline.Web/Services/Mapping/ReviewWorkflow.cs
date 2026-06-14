using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Mapping;

/// <summary>
/// The status transition applied when a reviewer saves corrections. Depends only on the
/// IDocumentRepository seam so it is unit-testable without a database: a NEEDS_REVIEW document
/// moves to VALIDATED (logging a VALIDATE PipelineEvent); other statuses are left unchanged
/// (corrections are still persisted by the caller). Never re-runs OCR.
/// </summary>
public static class ReviewWorkflow
{
    public static string Finalize(IDocumentRepository documents, Document doc, int corrections, int? byUserId)
    {
        if (doc.StatusCode == "NEEDS_REVIEW")
        {
            documents.SetStatus(doc.DocumentId, "VALIDATED");
            documents.LogEvent(doc.DocumentId, "VALIDATE", "NEEDS_REVIEW", "VALIDATED",
                $"Reviewer corrected {corrections} value(s)", byUserId);
            return "VALIDATED";
        }
        return doc.StatusCode;
    }
}
