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
/// Offline tests for the BackgroundService worker (no DB/network): startup re-enqueue + processing,
/// and graceful shutdown without hanging.
/// </summary>
public sealed class PipelineWorkerTests
{
    private sealed class FakeRepo : IDocumentRepository
    {
        private readonly Dictionary<long, string> _status = new();
        public void Seed(long id, string status) => _status[id] = status;
        public string? StatusOf(long id) => _status.TryGetValue(id, out var s) ? s : null;

        public Document? GetById(long id) => _status.TryGetValue(id, out var s) ? new Document { DocumentId = id, StatusCode = s } : null;
        public void SetStatus(long id, string statusCode) => _status[id] = statusCode;
        public void LogEvent(long id, string stage, string? from, string to, string? message, int? byUserId) { }
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
        public Task ProcessAsync(long id, int? byUserId, CancellationToken ct) => behavior(id, repo, ct);
    }

    private static PipelineWorker BuildWorker(FakeRepo repo, FakePipeline pipeline, out IJobQueue queue)
    {
        var sp = new ServiceCollection()
            .AddSingleton<IDocumentRepository>(repo)
            .AddSingleton<IPipelineRunner>(pipeline)
            .BuildServiceProvider();
        var opts = Options.Create(new QueueOptions { MaxConcurrency = 1, MaxAttempts = 1, BackoffBaseSeconds = 0, Capacity = 64 });
        queue = new ChannelJobQueue(opts);
        var runner = new JobRunner(sp.GetRequiredService<IServiceScopeFactory>(), opts, NullLogger<JobRunner>.Instance);
        return new PipelineWorker(queue, runner, sp.GetRequiredService<IServiceScopeFactory>(), opts, NullLogger<PipelineWorker>.Instance);
    }

    [Fact]
    public async Task Startup_reenqueues_captured_and_processes_it()
    {
        var done = new TaskCompletionSource();
        var repo = new FakeRepo();
        var pipeline = new FakePipeline(repo, (id, r, _) => { r.SetStatus(id, "MAPPED"); done.TrySetResult(); return Task.CompletedTask; });
        repo.Seed(7, "CAPTURED");                       // stuck at CAPTURED before the worker starts

        var worker = BuildWorker(repo, pipeline, out _);
        await worker.StartAsync(default);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);

        Assert.Equal("MAPPED", repo.StatusOf(7));
    }

    [Fact]
    public async Task Enqueued_document_is_processed()
    {
        var done = new TaskCompletionSource();
        var repo = new FakeRepo();
        var pipeline = new FakePipeline(repo, (id, r, _) => { r.SetStatus(id, "MAPPED"); done.TrySetResult(); return Task.CompletedTask; });

        var worker = BuildWorker(repo, pipeline, out var queue);
        await worker.StartAsync(default);
        repo.Seed(3, "CAPTURED");                       // seeded after start -> not in startup snapshot
        await queue.EnqueueAsync(3);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);

        Assert.Equal("MAPPED", repo.StatusOf(3));
    }

    [Fact]
    public async Task Graceful_shutdown_stops_the_worker_without_hanging()
    {
        var started = new TaskCompletionSource();
        var repo = new FakeRepo();
        var pipeline = new FakePipeline(repo, (_, _, ct) => { started.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); });
        repo.Seed(9, "CAPTURED");

        var worker = BuildWorker(repo, pipeline, out _);
        await worker.StartAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));   // the job is in flight, blocked

        // shutdown must complete promptly (the in-flight job cancels cleanly)
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("CAPTURED", repo.StatusOf(9));             // graceful -> re-enqueueable, not FAILED
    }
}
