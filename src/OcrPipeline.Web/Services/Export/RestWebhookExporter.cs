using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Services.Export;

/// <summary>
/// Posts the mapped JSON to a configured endpoint and signs the EXACT request body bytes with
/// HMAC-SHA256 (header "X-Signature: sha256=&lt;hex&gt;") using the target's secret, so a receiver
/// can verify the signature over the raw body it received. The secret/signature are never returned
/// in the <see cref="ExportAttempt"/> (and so never logged). Outbound HTTP is bounded by a
/// configurable timeout and honours the caller's CancellationToken (shutdown).
/// </summary>
public sealed class RestWebhookExporter(IHttpClientFactory httpClientFactory, IOptions<ExportOptions> options)
    : IExportTarget
{
    public string Kind => "REST_WEBHOOK";

    public async Task<ExportAttempt> SendAsync(Document document, string mappedJson, ExportTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Endpoint))
            return new ExportAttempt(false, null, "No endpoint configured");

        // Sign the exact bytes we POST.
        byte[] body = Encoding.UTF8.GetBytes(mappedJson ?? "");
        string signature = "sha256=" + ComputeHmacHex(body, target.AuthSecret ?? "");

        using var request = new HttpRequestMessage(HttpMethod.Post, target.Endpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Signature", signature);
        if (!string.IsNullOrWhiteSpace(target.AuthHeaderName))
            request.Headers.TryAddWithoutValidation(target.AuthHeaderName, target.AuthSecret);

        var client = httpClientFactory.CreateClient("export");

        // Per-request timeout, linked to the caller's token so shutdown cancels promptly.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds)));

        try
        {
            using var response = await client.SendAsync(request, cts.Token);
            string snippet = Truncate(await response.Content.ReadAsStringAsync(cts.Token), 500);
            return new ExportAttempt(response.IsSuccessStatusCode, (int)response.StatusCode, snippet);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // host shutdown — let the worker drain gracefully
        }
        catch (OperationCanceledException)
        {
            return new ExportAttempt(false, null, "Request timed out");   // our own timeout
        }
        catch (HttpRequestException ex)
        {
            // ex.Message is about transport/status — it does not contain the secret.
            return new ExportAttempt(false, null, Truncate("HTTP error: " + ex.Message, 500));
        }
    }

    private static string ComputeHmacHex(byte[] body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private static string? Truncate(string? s, int max)
        => s is null ? null : (s.Length <= max ? s : s[..max]);
}
