using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace OcrPipeline.Web.Services.Queue;

/// <summary>
/// In-process queue over a BOUNDED <see cref="Channel{T}"/>. Bounded (not unbounded) caps memory
/// under an upload flood; FullMode = Wait (not Drop) backpressures enqueuers so a job is never
/// silently lost. Lost on process restart — the worker re-enqueues CAPTURED documents on startup.
/// </summary>
public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<long> _channel;

    public ChannelJobQueue(IOptions<QueueOptions> options)
    {
        _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(Math.Max(1, options.Value.Capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(long documentId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(documentId, ct);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
