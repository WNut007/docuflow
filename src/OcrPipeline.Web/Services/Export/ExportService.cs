using Microsoft.Extensions.Logging;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Export;

/// <summary>
/// Runs every active export target for a document and records an ExportLog per attempt. The document
/// moves to CONSUMED only when ALL active targets succeed AND at least one ran — "0 targets" is NOT
/// "all succeeded" (guardrail 1). Only VALIDATED documents (or an already-CONSUMED one, for a
/// deliberate re-send) are exported; status is re-checked here so a stale enqueue is a no-op
/// (guardrail 2). Depends only on seams, so it is unit-testable without a DB/network.
/// </summary>
public sealed class ExportService(
    IDocumentRepository documents,
    IMappingRepository mapping,
    IExportRepository exports,
    IEnumerable<IExportTarget> exporters,
    ILogger<ExportService> logger)
{
    private readonly Dictionary<string, IExportTarget> _byKind =
        exporters.ToDictionary(e => e.Kind, StringComparer.OrdinalIgnoreCase);

    public async Task ExportAsync(long documentId, CancellationToken ct)
    {
        var doc = documents.GetById(documentId);
        if (doc is null) return;

        // Guardrail 2: only export VALIDATED (or re-send a CONSUMED) document.
        if (doc.StatusCode is not ("VALIDATED" or "CONSUMED"))
        {
            logger.LogInformation("Skipping export for document {Id}: status '{Status}' is not exportable.", documentId, doc.StatusCode);
            return;
        }

        var json = mapping.GetLatestResult(documentId)?.json;
        if (string.IsNullOrWhiteSpace(json))
        {
            logger.LogInformation("Skipping export for document {Id}: no mapped result to export.", documentId);
            return;
        }

        var targets = exports.GetActiveTargets(doc.DocumentTypeId);

        // Guardrail 1: nothing to consume to -> do NOT mark CONSUMED.
        if (targets.Count == 0)
        {
            documents.LogEvent(documentId, "CONSUME", doc.StatusCode, doc.StatusCode,
                "No active export target — nothing exported", null);
            return;
        }

        int ran = 0;
        bool allSucceeded = true;
        foreach (var target in targets)
        {
            ExportAttempt attempt = _byKind.TryGetValue(target.Kind, out var exporter)
                ? await exporter.SendAsync(doc, json, target, ct)   // may throw OCE on shutdown -> propagate
                : new ExportAttempt(false, null, $"No exporter registered for kind '{target.Kind}'");

            ran++;
            if (!attempt.Success) allSucceeded = false;

            exports.InsertLog(new ExportLog
            {
                DocumentId = documentId,
                TargetId = target.TargetId,
                StatusCode = attempt.Success ? "SUCCESS" : "FAILED",
                HttpStatus = attempt.HttpStatus,
                ResponseSnippet = attempt.ResponseSnippet,   // truncated response only — never a secret
                Attempt = 1
            });
        }

        if (ran >= 1 && allSucceeded)
        {
            documents.SetStatus(documentId, "CONSUMED");
            documents.LogEvent(documentId, "CONSUME", doc.StatusCode, "CONSUMED", $"Exported to {ran} target(s)", null);
        }
        // else: failures recorded in ExportLog; document stays VALIDATED and is re-exportable.
    }
}
