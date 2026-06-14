using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Data;

namespace OcrPipeline.Web.Services.Queue;

/// <summary>
/// Runs one queued document with retry/backoff, in a FRESH DI scope per attempt (scoped repos +
/// PipelineService resolved inside the scope). Cancellation handling:
///  - HOST stopping token cancelled (graceful shutdown): leave the document re-enqueueable
///    (reset to CAPTURED), do NOT mark FAILED, do NOT count as a failed attempt.
///  - cancellation NOT from the host stop (e.g. the Prompt-7 batch timeout): a real failure that
///    goes through the retry/FAILED path.
/// </summary>
public sealed class JobRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<QueueOptions> options,
    ILogger<JobRunner> logger)
{
    public async Task RunAsync(long documentId, CancellationToken stoppingToken)
    {
        int maxAttempts = Math.Max(1, options.Value.MaxAttempts);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var scope = scopeFactory.CreateScope();
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
            var pipeline = scope.ServiceProvider.GetRequiredService<IPipelineRunner>();

            // Guardrail 1: re-check current status inside the scope; only a still-queued (CAPTURED)
            // document is processed, so one picked up twice (e.g. startup re-enqueue racing a fresh
            // upload, or already processed by REALTIME) is never processed again.
            var doc = documents.GetById(documentId);
            if (doc is null || doc.StatusCode != "CAPTURED")
            {
                logger.LogInformation("Skipping document {Id}: status '{Status}' is not queued.", documentId, doc?.StatusCode);
                return;
            }

            bool failed;
            try
            {
                await pipeline.ProcessAsync(documentId, byUserId: null, stoppingToken);
                // PipelineService swallows non-cancellation failures and records FAILED itself.
                var after = documents.GetById(documentId);
                failed = after?.StatusCode == "FAILED";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — leave it re-enqueueable, do not fail it.
                ResetToQueued(documents, documentId);
                return;
            }
            catch (OperationCanceledException)
            {
                // Cancellation NOT from the host stop (batch timeout) -> real failure.
                logger.LogWarning("Document {Id} timed out (attempt {Attempt}/{Max}).", documentId, attempt, maxAttempts);
                failed = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pipeline threw for document {Id} (attempt {Attempt}/{Max}).", documentId, attempt, maxAttempts);
                failed = true;
            }

            if (!failed) return;   // success

            if (attempt < maxAttempts)
            {
                // Reset to CAPTURED so the document is re-enqueueable (a cancel during backoff leaves
                // it queued, not FAILED) and the retry starts from a clean queued state.
                documents.SetStatus(documentId, "CAPTURED");

                var delay = TimeSpan.FromSeconds(options.Value.BackoffBaseSeconds * Math.Pow(2, attempt - 1));
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Guardrail 2: cancelled during backoff -> stays CAPTURED, re-enqueued next startup.
                        return;
                    }
                }
            }
            else
            {
                // Final failure: ensure FAILED and log ONE queue-level give-up event. (The per-stage
                // FAILED event already came from PipelineService when it was a non-cancellation error.)
                documents.SetStatus(documentId, "FAILED");
                documents.LogEvent(documentId, "QUEUE", null, "FAILED", $"Gave up after {maxAttempts} attempt(s)", null);
            }
        }
    }

    private void ResetToQueued(IDocumentRepository documents, long documentId)
    {
        try { documents.SetStatus(documentId, "CAPTURED"); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to reset document {Id} to CAPTURED on shutdown.", documentId); }
    }
}
