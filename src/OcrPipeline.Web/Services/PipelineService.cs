using OcrPipeline.Web.Data;
using OcrPipeline.Web.Services.Mapping;

namespace OcrPipeline.Web.Services;

/// <summary>
/// Drives a document through the pipeline:
/// Classify -> Extract(OCR) -> derive properties -> Map(with transformers) -> ready.
/// In production each stage would be a queue worker; here it runs inline for the mockup.
/// </summary>
public sealed class PipelineService(
    DocumentRepository documents,
    OcrRepository ocrRepo,
    MappingRepository mappingRepo,
    ExtractionService extraction,
    MappingEngine mappingEngine)
{
    public async Task ProcessAsync(long documentId, int? byUserId, CancellationToken ct = default)
    {
        var doc = documents.GetById(documentId)
            ?? throw new InvalidOperationException($"Document {documentId} not found.");

        // 1) CLASSIFY (mockup: default to INVOICE type id = 1)
        const int classifiedTypeId = 1;
        documents.SetClassification(documentId, classifiedTypeId, 0.92m);
        documents.LogEvent(documentId, "CLASSIFY", "CAPTURED", "CLASSIFIED", "Auto-classified", byUserId);

        // 2) EXTRACT (OCR -> text + tables), then derive flat properties
        long runId = await extraction.ExtractAsync(documentId, doc.StoredPath, doc.ContentType, ct);
        var ocr = ocrRepo.LoadLatest(documentId);
        if (ocr is not null) ocrRepo.SaveProperties(documentId, runId, ocr);
        documents.SetStatus(documentId, "EXTRACTED");
        documents.LogEvent(documentId, "EXTRACT", "CLASSIFIED", "EXTRACTED", "OCR + properties extracted", byUserId);

        // 3) MAP (resolve fields, run transformer pipeline, build target model)
        var template = mappingRepo.GetActiveTemplateForType(classifiedTypeId);
        if (template is not null && ocr is not null)
        {
            var steps = mappingRepo.GetTransformerSteps(template.TemplateId);
            var outcome = await mappingEngine.RunAsync(template, ocr, steps, ct);
            mappingRepo.SaveResult(documentId, outcome);

            var nextStatus = outcome.NeedsReview ? "NEEDS_REVIEW" : "MAPPED";
            documents.SetStatus(documentId, nextStatus);
            documents.LogEvent(documentId, "MAP", "EXTRACTED", nextStatus,
                $"Mapped with confidence {outcome.OverallConfidence:P0}", byUserId);
        }
        else
        {
            documents.SetStatus(documentId, "FAILED");
            documents.LogEvent(documentId, "MAP", "EXTRACTED", "FAILED",
                "No active template or OCR result", byUserId);
        }
    }
}
