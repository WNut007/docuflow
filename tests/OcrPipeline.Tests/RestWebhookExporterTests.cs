using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Export;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>Offline tests for RestWebhookExporter (fake HttpMessageHandler — no real network).</summary>
public sealed class RestWebhookExporterTests
{
    private sealed class CapturingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public byte[] BodyBytes = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            if (request.Content is not null) BodyBytes = await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static RestWebhookExporter NewExporter(HttpMessageHandler handler)
        => new(new FakeFactory(handler), Options.Create(new ExportOptions { TimeoutSeconds = 5 }));

    [Fact]
    public async Task Posts_json_with_auth_header_and_correct_hmac_signature()
    {
        const string secret = "supersecret";
        const string json = "{\"invoice_id\":\"US-001\",\"total\":154.06}";
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"ok\":true}");
        var target = new ExportTarget { Endpoint = "https://example.com/hook", AuthHeaderName = "X-Api-Key", AuthSecret = secret };

        var attempt = await NewExporter(handler).SendAsync(new Document { DocumentId = 1 }, json, target, default);

        Assert.True(attempt.Success);
        Assert.Equal(200, attempt.HttpStatus);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://example.com/hook", handler.Request.RequestUri!.ToString());
        Assert.Equal(json, Encoding.UTF8.GetString(handler.BodyBytes));         // exact body posted

        Assert.True(handler.Request.Headers.TryGetValues("X-Api-Key", out var apiKey));
        Assert.Equal(secret, apiKey.Single());

        // Guardrail 3: the signature must verify against the LITERAL body bytes sent.
        Assert.True(handler.Request.Headers.TryGetValues("X-Signature", out var sigValues));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = "sha256=" + Convert.ToHexString(hmac.ComputeHash(handler.BodyBytes)).ToLowerInvariant();
        Assert.Equal(expected, sigValues.Single());
    }

    [Fact]
    public async Task Non_success_status_is_reported_as_failure()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "boom");
        var target = new ExportTarget { Endpoint = "https://example.com/hook", AuthSecret = "s" };

        var attempt = await NewExporter(handler).SendAsync(new Document { DocumentId = 1 }, "{}", target, default);

        Assert.False(attempt.Success);
        Assert.Equal(500, attempt.HttpStatus);
        Assert.Equal("boom", attempt.ResponseSnippet);
    }
}
