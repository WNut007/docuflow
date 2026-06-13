using Dapper;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Data;

public sealed class ProcessorRepository(SqlConnectionFactory factory)
{
    public IReadOnlyList<Processor> GetAll()
    {
        using var db = factory.Create();
        const string sql = """
            SELECT ProcessorId, Name, Engine, ProcessorMode, ConfigJson, StoreRawJson, IsActive
            FROM dbo.Processor
            ORDER BY Name;
            """;
        return db.Query<Processor>(sql).ToList();
    }

    public Processor? GetActiveForEngine(string engine)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT TOP 1 ProcessorId, Name, Engine, ProcessorMode, ConfigJson, StoreRawJson, IsActive
            FROM dbo.Processor
            WHERE Engine = @Engine AND IsActive = 1
            ORDER BY ProcessorId;
            """;
        return db.QuerySingleOrDefault<Processor>(sql, new { Engine = engine });
    }
}
