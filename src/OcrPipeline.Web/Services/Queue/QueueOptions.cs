namespace OcrPipeline.Web.Services.Queue;

/// <summary>Bound from configuration section "Ocr:Queue".</summary>
public sealed class QueueOptions
{
    /// <summary>How many documents process in parallel.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Total attempts per document before giving up (FAILED).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Exponential backoff base: delay = BackoffBaseSeconds * 2^(attempt-1).</summary>
    public double BackoffBaseSeconds { get; set; } = 2;

    /// <summary>Bounded channel capacity. Full = enqueuers backpressure (Wait), jobs are never dropped.</summary>
    public int Capacity { get; set; } = 1000;
}
