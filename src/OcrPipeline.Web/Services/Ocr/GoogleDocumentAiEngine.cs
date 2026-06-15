using System.Text;
using Google.Api.Gax.Grpc;
using Google.Cloud.DocumentAI.V1;
using Google.Cloud.Storage.V1;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using GcpDocument = Google.Cloud.DocumentAI.V1.Document;

namespace OcrPipeline.Web.Services.Ocr;

/// <summary>
/// Real Google Document AI engine. Small documents (&lt;= OnlinePageLimit) use the online
/// ProcessDocument endpoint; larger ones route to BATCH via Google Cloud Storage
/// (BatchProcessDocuments). Both paths map the returned Document proto with the SAME
/// <see cref="DocumentAiMapper"/>.
///
/// Auth: Application Default Credentials (GOOGLE_APPLICATION_CREDENTIALS or
/// `gcloud auth application-default login`). The service account needs role
/// roles/documentai.apiUser, plus object admin on the batch bucket
/// (roles/storage.objectAdmin) when batch is enabled.
///
/// NuGet: Google.Cloud.DocumentAI.V1, Google.Cloud.Storage.V1
/// </summary>
public sealed class GoogleDocumentAiEngine(
    IOptions<GoogleDocAiOptions> options,
    DocumentAiMapper mapper,
    IPdfPageCounter pageCounter,
    ILogger<GoogleDocumentAiEngine> logger) : IOcrEngine
{
    private readonly GoogleDocAiOptions _o = options.Value;

    public string Name => "GOOGLE_DOCAI";

    // languages: ignored — Document AI auto-detects script (incl. Thai). Present to satisfy IOcrEngine.
    public async Task<OcrExtraction> ExtractAsync(string filePath, string contentType, string? languages = null, CancellationToken ct = default)
    {
        int pages = pageCounter.CountPages(filePath, contentType);

        if (pages > _o.OnlinePageLimit)
        {
            if (!BatchRouting.ShouldBatch(pages, _o))
                throw new InvalidOperationException(
                    $"Document has {pages} pages (over the online limit of {_o.OnlinePageLimit}), but no " +
                    "Ocr:GoogleDocAi:Bucket is configured for batch processing.");
            return await ExtractBatchAsync(filePath, contentType, ct);
        }

        return await ExtractOnlineAsync(filePath, contentType, ct);
    }

    // ---- online -------------------------------------------------------------
    private async Task<OcrExtraction> ExtractOnlineAsync(string filePath, string contentType, CancellationToken ct)
    {
        var client = await BuildClientAsync(ct);
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var request = new ProcessRequest
        {
            Name = ProcessorName(),
            RawDocument = new RawDocument
            {
                Content = ByteString.CopyFrom(bytes),
                MimeType = Mime(contentType)
            }
        };

        var response = await client.ProcessDocumentAsync(request, ct);
        return mapper.Map(response.Document, Name, _o.ProcessorVersion ?? "default");
    }

    // ---- batch via GCS ------------------------------------------------------
    private async Task<OcrExtraction> ExtractBatchAsync(string filePath, string contentType, CancellationToken ct)
    {
        // Bound the whole batch operation by a configurable timeout (and the caller's token).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _o.BatchTimeoutMinutes)));
        var token = timeoutCts.Token;

        var storage = await StorageClient.CreateAsync();
        var client = await BuildClientAsync(token);

        // Unique per-run guid folder for BOTH input and output so concurrent runs never collide and
        // the output listing only returns THIS run's shards.
        string runId = Guid.NewGuid().ToString("N");
        string ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) ext = ".pdf";
        string inputObject = $"{Trim(_o.InputPrefix)}/{runId}/source{ext}";
        string outputFolder = $"{Trim(_o.OutputPrefix)}/{runId}/";
        string inputUri = $"gs://{_o.Bucket}/{inputObject}";
        string outputUri = $"gs://{_o.Bucket}/{outputFolder}";

        var outputObjects = new List<string>();
        bool succeeded = false;
        try
        {
            // 1) upload the source file
            await using (var fs = File.OpenRead(filePath))
                await storage.UploadObjectAsync(_o.Bucket, inputObject, Mime(contentType), fs, cancellationToken: token);

            // 2) batch process (long-running operation), polling with cancellation/timeout
            var request = new BatchProcessRequest
            {
                Name = ProcessorName(),
                InputDocuments = new BatchDocumentsInputConfig
                {
                    GcsDocuments = new GcsDocuments
                    {
                        Documents = { new GcsDocument { GcsUri = inputUri, MimeType = Mime(contentType) } }
                    }
                },
                DocumentOutputConfig = new DocumentOutputConfig
                {
                    GcsOutputConfig = new DocumentOutputConfig.Types.GcsOutputConfig { GcsUri = outputUri }
                }
            };

            var op = await client.BatchProcessDocumentsAsync(request, token);
            var completed = await op.PollUntilCompletedAsync(null, CallSettings.FromCancellationToken(token));
            if (completed.IsFaulted)
                throw new InvalidOperationException(
                    $"Document AI batch operation failed: {completed.Exception?.Message ?? "unknown error"}");

            // 3) read output Document JSON shards from this run's folder, map each with the shared mapper
            var shards = new List<GcpDocument>();
            await foreach (var obj in storage.ListObjectsAsync(_o.Bucket, outputFolder).WithCancellation(token))
            {
                outputObjects.Add(obj.Name);
                if (!obj.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                using var ms = new MemoryStream();
                await storage.DownloadObjectAsync(_o.Bucket, obj.Name, ms, options: null, cancellationToken: token);
                shards.Add(GcpDocument.Parser.ParseJson(Encoding.UTF8.GetString(ms.ToArray())));
            }

            var ex = new OcrExtraction { Engine = Name, EngineVersion = (_o.ProcessorVersion ?? "default") + "/batch" };
            int offset = 0;
            // order by the proto's authoritative shard index (GCS lists names lexicographically,
            // which would misorder multi-digit shard suffixes like -10 before -2)
            foreach (var shard in shards.OrderBy(s => s.ShardInfo?.ShardIndex ?? 0))
                offset += mapper.MapInto(ex, shard, offset);
            ex.PageCount = offset;
            mapper.Normalize(ex);
            ex.RawJson = $"{{\"batch\":\"{runId}\",\"shards\":{shards.Count}}}";

            succeeded = true;
            return ex;
        }
        finally
        {
            // On success: best-effort cleanup — a delete failure logs a warning but must NOT fail
            // the extraction (OCR already succeeded). On failure: leave objects for debugging.
            if (succeeded)
            {
                try
                {
                    await storage.DeleteObjectAsync(_o.Bucket, inputObject, cancellationToken: CancellationToken.None);
                    foreach (var name in outputObjects)
                        await storage.DeleteObjectAsync(_o.Bucket, name, cancellationToken: CancellationToken.None);
                }
                catch (Exception cleanupEx)
                {
                    logger.LogWarning(cleanupEx,
                        "Batch GCS cleanup failed for run {RunId}; objects left under gs://{Bucket}/.../{RunId}/",
                        runId, _o.Bucket);
                }
            }
        }
    }

    // ---- helpers ------------------------------------------------------------
    private Task<DocumentProcessorServiceClient> BuildClientAsync(CancellationToken ct)
        => new DocumentProcessorServiceClientBuilder
        {
            Endpoint = $"{_o.Location}-documentai.googleapis.com"
        }.BuildAsync(ct);

    private string ProcessorName()
        => string.IsNullOrWhiteSpace(_o.ProcessorVersion)
            ? $"projects/{_o.ProjectId}/locations/{_o.Location}/processors/{_o.ProcessorId}"
            : $"projects/{_o.ProjectId}/locations/{_o.Location}/processors/{_o.ProcessorId}/processorVersions/{_o.ProcessorVersion}";

    private static string Mime(string contentType) => string.IsNullOrWhiteSpace(contentType) ? "application/pdf" : contentType;
    private static string Trim(string prefix) => prefix.Trim('/');
}
