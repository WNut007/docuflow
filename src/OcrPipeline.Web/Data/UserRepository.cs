using Dapper;
using OcrPipeline.Web.Domain;

namespace OcrPipeline.Web.Data;

public sealed class UserRepository(SqlConnectionFactory factory)
{
    public AppUser? FindByEmail(string email)
    {
        using var db = factory.Create();
        // Parameterized — value bound by Dapper, never concatenated into SQL.
        const string sql = """
            SELECT UserId, UserName, Email, PasswordHash, DisplayName, IsActive
            FROM dbo.AppUser
            WHERE Email = @Email AND IsActive = 1;
            """;
        return db.QuerySingleOrDefault<AppUser>(sql, new { Email = email });
    }

    public IReadOnlyList<string> GetRoleNames(int userId)
    {
        using var db = factory.Create();
        const string sql = """
            SELECT r.RoleName
            FROM dbo.AppUserRole ur
            JOIN dbo.AppRole r ON r.RoleId = ur.RoleId
            WHERE ur.UserId = @UserId;
            """;
        return db.Query<string>(sql, new { UserId = userId }).ToList();
    }

    public void UpdateLastLogin(int userId)
    {
        using var db = factory.Create();
        const string sql = "UPDATE dbo.AppUser SET LastLoginUtc = SYSUTCDATETIME() WHERE UserId = @UserId;";
        db.Execute(sql, new { UserId = userId });
    }
}
