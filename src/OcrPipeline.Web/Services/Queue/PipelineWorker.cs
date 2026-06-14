using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Data;

namespace OcrPipeline.Web.Services.Queue;

/// <summary>
/// Background worker that drains the job queue with bounded concurrency. On startup it re-enqueues
/// documents stuck at CAPTURED (the in-process queue is lost on restart). The host stopping token is
/// passed through to each job so a long batch operation cancels cleanly on shutdown.
/// </summary>
public sealed class PipelineWorker(
    IJobQueue queue,
    JobRunner runner,
    IServiceScopeFactory scopeFactory,
    IOptions<QueueOptions> options,
    ILogger<PipelineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start consumers first so the (possibly large) startup re-enqueue can drain into a bounded
        // channel without deadlocking on a full queue.
        int n = Math.Max(1, options.Value.MaxConcurrency);
        var consumers = Enumerable.Range(0, n).Select(_ => ConsumeAsync(stoppingToken)).ToArray();

        await RequeueCapturedAsync(stoppingToken);
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var documentId in queue.ReadAllAsync(stoppingToken))
                await runner.RunAsync(documentId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful drain on shutdown
        }
    }

    private async Task RequeueCapturedAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
            var captured = documents.GetByStatus("CAPTURED");
            if (captured.Count > 0)
                logger.LogInformation("Re-enqueueing {Count} document(s) stuck at CAPTURED on startup.", captured.Count);

            foreach (var doc in captured)
                await queue.EnqueueAsync(doc.DocumentId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup re-enqueue of CAPTURED documents failed.");
        }
    }
}
