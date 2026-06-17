using Microsoft.Extensions.Logging;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Services.Mapping;

namespace OcrPipeline.Web.Services;

/// <summary>
/// Drives a document through the pipeline:
/// Classify -> Extract(OCR) -> derive properties -> Map(with transformers) -> ready.
/// In production each stage would be a queue worker; here it runs inline for the mockup.
///
/// Any unhandled stage error marks the document FAILED and logs a PipelineEvent (consistent
/// with how the MAP stage records a handled failure) instead of propagating — so an upload that
/// hits, e.g., Tesseract's fail-fast does not 500: the caller still lands on the Detail page
/// showing FAILED. Full exception detail goes to ILogger only, never to the user-facing event.
/// </summary>
public sealed class PipelineService(
    IDocumentRepository documents,
    OcrRepository ocrRepo,
    IMappingRepository mappingRepo,
    ExtractionService extraction,
    MappingEngine mappingEngine,
    OcrPipeline.Web.Services.Zonal.ZonalExtractionService zonal,
    ILogger<PipelineService> logger) : IPipelineRunner
{
    public async Task ProcessAsync(long documentId, int? byUserId, CancellationToken ct = default)
    {
        string stage = "CLASSIFY";
        try
        {
            var doc = documents.GetById(documentId)
                ?? throw new InvalidOperationException($"Document {documentId} not found.");

            // 1) CLASSIFY (mockup: default to INVOICE type id = 1). Then RESOLVE which template of
            // that type this document uses: the user's explicit pick at upload (doc.TemplateId) is
            // required and wins; an absent/wrong-type pick resolves to null. (No page-count guessing —
            // page POSITION roles live in PageRoleResolver inside the multi-page path.) This stops two
            // layouts from sharing one template and clobbering each other's zones.
            const int classifiedTypeId = 1;
            var candidates = mappingRepo.GetTemplatesForType(classifiedTypeId);
            var chosenTemplateId = TemplateResolver.Resolve(doc.TemplateId, candidates);
            var template = chosenTemplateId is { } tid ? mappingRepo.GetTemplateById(tid) : null;

            documents.SetClassification(documentId, template?.DocumentTypeId ?? classifiedTypeId, 0.92m);
            documents.LogEvent(documentId, "CLASSIFY", "CAPTURED", "CLASSIFIED",
                template is null ? "Auto-classified (no template)" : $"Auto-classified -> template '{template.Name}'",
                byUserId);

            // ZONAL templates skip full-page OCR entirely: OCR only inside each drawn zone, straight
            // into the field. (No reliance on whatever blocks Tesseract's layout analysis produces.)
            if (template is not null &&
                string.Equals(template.MappingMode, "ZONAL", StringComparison.OrdinalIgnoreCase))
            {
                stage = "EXTRACT";
                var zsteps = mappingRepo.GetTransformerSteps(template.TemplateId);
                var zcols = mappingRepo.GetTableColumns(template.TemplateId); // line_item table zones

                // Multi-page (Phase 3): a template that tags any zone with a page-role
                // (FIRST/CONTINUATION/LAST) is read per page-role and its line_item rows concatenated.
                // Legacy templates (all roles null) keep the unchanged single-page path verbatim.
                var zoutcome = OcrPipeline.Web.Services.Zonal.ZonalRouting.IsMultiPage(template)
                    ? await zonal.ProcessMultiPageAsync(doc, template, zsteps, zcols, ct)
                    : await zonal.ProcessAsync(doc, template, zsteps, zcols, ct);
                mappingRepo.SaveResult(documentId, zoutcome);

                var znext = zoutcome.NeedsReview ? "NEEDS_REVIEW" : "MAPPED";
                documents.SetStatus(documentId, znext);
                documents.LogEvent(documentId, "MAP", "CLASSIFIED", znext,
                    $"Zonal mapped with confidence {zoutcome.OverallConfidence:P0}", byUserId);
                return;
            }

            // 2) EXTRACT (OCR -> text + tables), then derive flat properties  [OCR-first path]
            stage = "EXTRACT";
            long runId = await extraction.ExtractAsync(documentId, doc.StoredPath, doc.ContentType, doc.OcrLanguages, ct);
            var ocr = ocrRepo.LoadLatest(documentId);
            if (ocr is not null) ocrRepo.SaveProperties(documentId, runId, ocr);
            documents.SetStatus(documentId, "EXTRACTED");
            documents.LogEvent(documentId, "EXTRACT", "CLASSIFIED", "EXTRACTED", "OCR + properties extracted", byUserId);

            // 3) MAP (resolve fields, run transformer pipeline, build target model)
            stage = "MAP";
            if (template is not null && ocr is not null)
            {
                var steps = mappingRepo.GetTransformerSteps(template.TemplateId);
                var columns = mappingRepo.GetTableColumns(template.TemplateId);
                var outcome = await mappingEngine.RunAsync(template, ocr, steps, columns, ct);
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
        catch (OperationCanceledException)
        {
            // Cancellation (host shutdown or the batch-timeout token) is NOT a stage failure — let
            // the caller (queue worker) decide whether it's a graceful shutdown or a real timeout.
            throw;
        }
        catch (Exception ex)
        {
            // Any unhandled stage error -> FAILED. Full detail to the log; short reason to the event.
            logger.LogError(ex, "Pipeline stage {Stage} failed for document {DocumentId}", stage, documentId);
            documents.SetStatus(documentId, "FAILED");
            documents.LogEvent(documentId, stage, null, "FAILED", FailureMessage(stage, ex), byUserId);
        }
    }

    /// <summary>Short, single-line, stack-trace-free reason suitable for a user-visible event.</summary>
    private static string FailureMessage(string stage, Exception ex)
    {
        string label = stage switch
        {
            "CLASSIFY" => "Classification",
            "EXTRACT" => "Extraction",
            "MAP" => "Mapping",
            _ => stage
        };
        string reason = ex.Message;
        int nl = reason.IndexOfAny(['\r', '\n']);
        if (nl >= 0) reason = reason[..nl];
        if (reason.Length > 200) reason = reason[..200];
        return $"{label} failed: {reason}";
    }
}
