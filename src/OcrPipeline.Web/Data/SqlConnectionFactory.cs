using System.Data;
using Microsoft.Data.SqlClient;

namespace OcrPipeline.Web.Data;

/// <summary>
/// Hands out fresh open SqlConnection instances for Dapper.
/// Registered as a singleton; connections themselves are short-lived per call.
/// </summary>
public sealed class SqlConnectionFactory(string connectionString)
{
    public IDbConnection Create()
    {
        var conn = new SqlConnection(connectionString);
        conn.Open();
        return conn;
    }
}
