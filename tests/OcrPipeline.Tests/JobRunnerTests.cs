using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services;
using OcrPipeline.Web.Services.Queue;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline tests for the queue JobRunner (no DB/network): scope-per-job, retry/backoff, and the
/// two cancellation sources — host shutdown (graceful, re-enqueueable) vs batch timeout (FAILED).
/// </summary>
public sealed class JobRunnerTests
{
    // In-memory IDocumentRepository tracking status + events.
    private sealed class FakeRepo : IDocumentRepository
    {
        private readonly Dictionary<long, string> _status = new();
        public List<(string Stage, string To, string? Message)> Events { get; } = [];

        public void Seed(long id, string status) => _status[id] = status;
        public string? StatusOf(long id) => _status.TryGetValue(id, out var s) ? s : null;

        public Document? GetById(long id) => _status.TryGetValue(id, out var s) ? new Document { DocumentId = id, StatusCode = s } : null;
        public void SetStatus(long id, string statusCode) => _status[id] = statusCode;
        public void LogEvent(long id, string stage, string? from, string to, string? message, int? byUserId) => Events.Add((stage, to, message));
        public IReadOnlyList<Document> GetByStatus(string statusCode)
            => _status.Where(kv => kv.Value == statusCode).Select(kv => new Document { DocumentId = kv.Key, StatusCode = kv.Value }).ToList();

        public long Insert(Document d) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetRecent(int top = 50) => throw new NotSupportedException();
        public IReadOnlyList<DocumentRef> GetByTypeWithPreviews(int t, int top = 20) => throw new NotSupportedException();
        public void InsertPages(long id, IEnumerable<DocumentPage> p) => throw new NotSupportedException();
        public IReadOnlyList<DocumentPage> GetPages(long id) => throw new NotSupportedException();
        public void SetClassification(long id, int t, decimal c) => throw new NotSupportedException();
    }

    private sealed class FakePipeline(FakeRepo repo, Func<long, FakeRepo, CancellationToken, Task> behavior) : IPipelineRunner
    {
        public int Calls { get; private set; }
        public Task ProcessAsync(long documentId, int? byUserId, CancellationToken ct) { Calls++; return behavior(documentId, repo, ct); }
    }

    private static (JobRunner runner, FakeRepo repo, FakePipeline pipeline) Build(
        Func<long, FakeRepo, CancellationToken, Task> behavior, int maxAttempts = 3, double backoff = 0)
    {
        var repo = new FakeRepo();
        var pipeline = new FakePipeline(repo, behavior);
        var sp = new ServiceCollection()
            .AddSingleton<IDocumentRepository>(repo)
            .AddSingleton<IPipelineRunner>(pipeline)
            .BuildServiceProvider();
        var opts = Options.Create(new QueueOptions { MaxAttempts = maxAttempts, BackoffBaseSeconds = backoff });
        var runner = new JobRunner(sp.GetRequiredService<IServiceScopeFactory>(), opts, NullLogger<JobRunner>.Instance);
        return (runner, repo, pipeline);
    }

    [Fact]
    public async Task Processes_a_queued_document()
    {
        var (runner, repo, pipeline) = Build((id, r, _) => { r.SetStatus(id, "MAPPED"); return Task.CompletedTask; });
        repo.Seed(1, "CAPTURED");

        await runner.RunAsync(1, CancellationToken.None);

        Assert.Equal(1, pipeline.Calls);
        Assert.Equal("MAPPED", repo.StatusOf(1));
        Assert.DoesNotContain(repo.Events, e => e.To == "FAILED");
    }

    [Fact]
    public async Task Skips_when_document_is_no_longer_queued()
    {
        var (runner, repo, pipeline) = Build((id, r, _) => { r.SetStatus(id, "FAILED"); return Task.CompletedTask; });
        repo.Seed(1, "MAPPED");   // already processed (e.g. by REALTIME or a prior pickup)

        await runner.RunAsync(1, CancellationToken.None);

        Assert.Equal(0, pipeline.Calls);          // never processed twice
        Assert.Equal("MAPPED", repo.StatusOf(1));
    }

    [Fact]
    public async Task Retries_then_marks_FAILED_after_max_attempts()
    {
        var (runner, repo, pipeline) = Build((id, r, _) => { r.SetStatus(id, "FAILED"); return Task.CompletedTask; },
            maxAttempts: 3, backoff: 0);
        repo.Seed(1, "CAPTURED");

        await runner.RunAsync(1, CancellationToken.None);

        Assert.Equal(3, pipeline.Calls);          // retried up to max
        Assert.Equal("FAILED", repo.StatusOf(1));
        Assert.Contains(repo.Events, e => e.Stage == "QUEUE" && e.To == "FAILED");  // one give-up event
    }

    // ---- Guardrail 3: distinguish the two cancellation sources ----------------

    [Fact]
    public async Task Host_shutdown_is_graceful_and_leaves_the_doc_re_enqueueable()
    {
        // pipeline blocks on the token; cancelling the HOST stopping token = graceful shutdown
        var (runner, repo, pipeline) = Build((_, _, ct) => Task.Delay(Timeout.Infinite, ct), maxAttempts: 3);
        repo.Seed(1, "CAPTURED");

        using var cts = new CancellationTokenSource();
        var task = runner.RunAsync(1, cts.Token);
        cts.CancelAfter(50);
        await task;

        Assert.Equal(1, pipeline.Calls);
        Assert.Equal("CAPTURED", repo.StatusOf(1));                       // NOT FAILED — stays queued
        Assert.DoesNotContain(repo.Events, e => e.To == "FAILED");        // no give-up
        Assert.Contains(repo.GetByStatus("CAPTURED"), d => d.DocumentId == 1);   // re-enqueueable
    }

    [Fact]
    public async Task Batch_timeout_cancellation_goes_to_the_FAILED_path()
    {
        // OperationCanceledException while the HOST token is NOT cancelled = the Prompt-7 batch
        // timeout = a real failure.
        var (runner, repo, pipeline) = Build((_, _, _) => throw new OperationCanceledException("batch timed out"),
            maxAttempts: 1);
        repo.Seed(1, "CAPTURED");

        await runner.RunAsync(1, CancellationToken.None);   // host token never cancelled

        Assert.Equal(1, pipeline.Calls);
        Assert.Equal("FAILED", repo.StatusOf(1));                          // treated as failure
        Assert.Contains(repo.Events, e => e.Stage == "QUEUE" && e.To == "FAILED");
    }

    [Fact]
    public async Task Cancel_during_backoff_stays_queued()
    {
        // attempt 1 fails, long backoff before retry; cancelling (host) during backoff leaves it queued
        var (runner, repo, pipeline) = Build((id, r, _) => { r.SetStatus(id, "FAILED"); return Task.CompletedTask; },
            maxAttempts: 2, backoff: 100);
        repo.Seed(1, "CAPTURED");

        using var cts = new CancellationTokenSource();
        var task = runner.RunAsync(1, cts.Token);
        cts.CancelAfter(50);
        await task;

        Assert.Equal(1, pipeline.Calls);                  // did not retry
        Assert.Equal("CAPTURED", repo.StatusOf(1));       // reset before backoff -> still queued
        Assert.DoesNotContain(repo.Events, e => e.To == "FAILED");
    }
}
