using Microsoft.Extensions.Logging.Abstractions;
using OcrPipeline.Web.Data;
using OcrPipeline.Web.Domain;
using OcrPipeline.Web.Services.Export;
using OcrPipeline.Web.Services.Mapping;
using OcrPipeline.Web.Services.Transform;
using Xunit;

namespace OcrPipeline.Tests;

/// <summary>
/// Offline tests for ExportService (fakes — no DB/network): CONSUMED only when all targets succeed
/// and ≥1 ran; failures record a FAILED log and do NOT mark CONSUMED; no targets ≠ consumed.
/// </summary>
public sealed class ExportServiceTests
{
    private sealed class FakeDocs : IDocumentRepository
    {
        private readonly Dictionary<long, string> _status = new();
        public List<(string Stage, string To, string? Message)> Events { get; } = [];
        public void Seed(long id, string status) => _status[id] = status;
        public string? StatusOf(long id) => _status.GetValueOrDefault(id);

        public Document? GetById(long id) => _status.TryGetValue(id, out var s) ? new Document { DocumentId = id, StatusCode = s, DocumentTypeId = 1 } : null;
        public void SetStatus(long id, string statusCode) => _status[id] = statusCode;
        public void LogEvent(long id, string stage, string? from, string to, string? message, int? byUserId) => Events.Add((stage, to, message));
        public long Insert(Document d) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetRecent(int top = 50) => throw new NotSupportedException();
        public IReadOnlyList<Document> GetByStatus(string statusCode) => throw new NotSupportedException();
        public IReadOnlyList<DocumentRef> GetByTypeWithPreviews(int t, int top = 20) => throw new NotSupportedException();
        public void InsertPages(long id, IEnumerable<DocumentPage> p) => throw new NotSupportedException();
        public IReadOnlyList<DocumentPage> GetPages(long id) => throw new NotSupportedException();
        public void SetClassification(long id, int t, decimal c) => throw new NotSupportedException();
    }

    private sealed class FakeMapping(string? json) : IMappingRepository
    {
        public (decimal? overall, bool needsReview, string? json, List<MappedValueRow> values)? GetLatestResult(long d)
            => json is null ? null : (null, false, json, []);
        public Dictionary<int, List<TransformerStep>> GetTransformerSteps(int t) => throw new NotSupportedException();
        public Dictionary<int, List<MappingTableColumn>> GetTableColumns(int t) => throw new NotSupportedException();
        public void SaveTableColumns(int fieldId, IEnumerable<MappingTableColumn> c) => throw new NotSupportedException();
        public MappingTemplate? GetActiveTemplateForType(int t) => throw new NotSupportedException();
        public IReadOnlyList<(MappingTemplate tpl, string docType, int fieldCount)> GetAllTemplates() => throw new NotSupportedException();
        public MappingTemplate? GetTemplateById(int t) => throw new NotSupportedException();
        public IReadOnlyList<string> GetPropertyKeysForType(int t) => throw new NotSupportedException();
        public void SaveFields(int t, IEnumerable<MappingField> f, IReadOnlyDictionary<int, List<TransformerStep>> s) => throw new NotSupportedException();
        public int UpsertFieldBinding(int t, MappingField f, bool b) => throw new NotSupportedException();
        public void SaveZones(int t, string mappingMode, IEnumerable<MappingField> f) => throw new NotSupportedException();
        public long SaveResult(long d, MappingOutcome o) => throw new NotSupportedException();
        public int UpdateResultValue(long d, long rv, string? n) => throw new NotSupportedException();
    }

    private sealed class FakeExports(params ExportTarget[] targets) : IExportRepository
    {
        public List<ExportLog> Logs { get; } = [];
        public IReadOnlyList<ExportTarget> GetActiveTargets(int? documentTypeId) => targets;
        public long InsertLog(ExportLog log) { Logs.Add(log); return Logs.Count; }
        public IReadOnlyList<ExportTarget> GetAllTargets() => targets;
        public ExportTarget? GetTargetById(int targetId) => null;
        public IReadOnlyList<ExportLog> GetLogsForDocument(long documentId) => Logs;
        public IReadOnlyList<ExportLog> GetRecentLogs(int top = 50) => Logs;
    }

    private sealed class FakeTarget(string kind, bool success) : IExportTarget
    {
        public string Kind => kind;
        public Task<ExportAttempt> SendAsync(Document d, string json, ExportTarget t, CancellationToken ct)
            => Task.FromResult(new ExportAttempt(success, success ? 200 : 500, success ? "ok" : "fail"));
    }

    private static ExportService NewService(FakeDocs docs, FakeExports exports, params IExportTarget[] exporters)
        => new(docs, new FakeMapping("{\"x\":1}"), exports, exporters, NullLogger<ExportService>.Instance);

    [Fact]
    public async Task All_targets_succeed_marks_CONSUMED_and_logs()
    {
        var docs = new FakeDocs(); docs.Seed(1, "VALIDATED");
        var exports = new FakeExports(new ExportTarget { TargetId = 5, Kind = "REST_WEBHOOK" });

        await NewService(docs, exports, new FakeTarget("REST_WEBHOOK", success: true)).ExportAsync(1, default);

        var log = Assert.Single(exports.Logs);
        Assert.Equal("SUCCESS", log.StatusCode);
        Assert.Equal("CONSUMED", docs.StatusOf(1));
        Assert.Contains(docs.Events, e => e.Stage == "CONSUME" && e.To == "CONSUMED");
    }

    [Fact]
    public async Task Failed_target_records_failed_log_and_does_not_mark_CONSUMED()
    {
        var docs = new FakeDocs(); docs.Seed(1, "VALIDATED");
        var exports = new FakeExports(new ExportTarget { TargetId = 5, Kind = "REST_WEBHOOK" });

        await NewService(docs, exports, new FakeTarget("REST_WEBHOOK", success: false)).ExportAsync(1, default);

        var log = Assert.Single(exports.Logs);
        Assert.Equal("FAILED", log.StatusCode);
        Assert.Equal(500, log.HttpStatus);
        Assert.Equal("VALIDATED", docs.StatusOf(1));                  // NOT consumed
        Assert.DoesNotContain(docs.Events, e => e.To == "CONSUMED");
    }

    [Fact]
    public async Task No_active_target_does_not_mark_CONSUMED()
    {
        var docs = new FakeDocs(); docs.Seed(1, "VALIDATED");
        var exports = new FakeExports();   // zero targets

        await NewService(docs, exports).ExportAsync(1, default);

        Assert.Empty(exports.Logs);
        Assert.Equal("VALIDATED", docs.StatusOf(1));                  // "0 targets" is not "all succeeded"
        Assert.Contains(docs.Events, e => e.Stage == "CONSUME" && e.Message!.Contains("nothing exported"));
    }

    [Fact]
    public async Task Skips_documents_that_are_not_exportable()
    {
        var docs = new FakeDocs(); docs.Seed(1, "MAPPED");           // not VALIDATED/CONSUMED
        var exports = new FakeExports(new ExportTarget { TargetId = 5, Kind = "REST_WEBHOOK" });

        await NewService(docs, exports, new FakeTarget("REST_WEBHOOK", success: true)).ExportAsync(1, default);

        Assert.Empty(exports.Logs);
        Assert.Equal("MAPPED", docs.StatusOf(1));
    }
}
