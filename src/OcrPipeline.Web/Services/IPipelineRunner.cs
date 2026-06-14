namespace OcrPipeline.Web.Services;

/// <summary>
/// Runs a document through the pipeline. Abstraction so the queue worker can be unit-tested with a
/// fake, and so the worker/controller don't depend on the concrete PipelineService.
/// </summary>
public interface IPipelineRunner
{
    Task ProcessAsync(long documentId, int? byUserId, CancellationToken ct = default);
}
