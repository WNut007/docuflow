namespace OcrPipeline.Web.Services.Queue;

/// <summary>
/// The job queue seam. The in-process <see cref="ChannelJobQueue"/> backs it today; swap in a
/// durable implementation (Azure Storage Queue / RabbitMQ / Hangfire) without touching the worker,
/// JobRunner, or PipelineService — they only know this interface.
/// </summary>
public interface IJobQueue
{
    /// <summary>Enqueue a document id for processing. Backpressures (awaits) when the queue is full.</summary>
    ValueTask EnqueueAsync(long documentId, CancellationToken ct = default);

    /// <summary>Streams enqueued document ids until cancelled.</summary>
    IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct);
}
