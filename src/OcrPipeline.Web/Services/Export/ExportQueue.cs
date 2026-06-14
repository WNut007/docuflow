using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OcrPipeline.Web.Services.Export;

/// <summary>Export job queue seam (same shape as the pipeline queue). Swap for a durable backend.</summary>
public interface IExportQueue
{
    ValueTask EnqueueAsync(long documentId, CancellationToken ct = default);
    IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct);
}

/// <summary>In-process bounded channel (FullMode = Wait so jobs are never dropped).</summary>
public sealed class ChannelExportQueue : IExportQueue
{
    private readonly Channel<long> _channel;

    public ChannelExportQueue(IOptions<ExportOptions> options)
        => _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(Math.Max(1, options.Value.Capacity))
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    public ValueTask EnqueueAsync(long documentId, CancellationToken ct = default) => _channel.Writer.WriteAsync(documentId, ct);
    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Drains export jobs off the request thread with bounded concurrency, a fresh DI scope per job, and
/// the host stopping token flowed into ExportService -> the exporter HTTP call, so a hanging webhook
/// cancels cleanly on shutdown (single attempt; manual re-export is the retry).
/// </summary>
public sealed class ExportWorker(
    IExportQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<ExportOptions> options,
    ILogger<ExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int n = Math.Max(1, options.Value.MaxConcurrency);
        var consumers = Enumerable.Range(0, n).Select(_ => ConsumeAsync(stoppingToken)).ToArray();
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var documentId in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var export = scope.ServiceProvider.GetRequiredService<ExportService>();
                    await export.ExportAsync(documentId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Export job for document {Id} failed unexpectedly.", documentId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful drain on shutdown
        }
    }
}
