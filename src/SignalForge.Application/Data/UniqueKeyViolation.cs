using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SignalForge.Application.Data;

/// <summary>
/// Shared detection of relational unique-constraint violations, used where an idempotency write
/// races a concurrent duplicate (event ingest, dead-letter replay).
/// SQL Server reports these via <see cref="SqlException.Number"/> on the provider exception:
/// 2601 (unique index "Cannot insert duplicate key row") / 2627 (primary-key constraint). The
/// SQLSTATE column on the base <see cref="System.Data.Common.DbException"/> is left empty by
/// Microsoft.Data.SqlClient, so the number — not SqlState — is authoritative for SQL Server.
/// Postgres reports them via SQLSTATE 23505 on <see cref="System.Data.Common.DbException.SqlState"/>.
/// </summary>
public static class UniqueKeyViolation
{
    /// <summary>Returns true when <paramref name="ex"/> was caused by a unique index/constraint.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        switch (ex.InnerException)
        {
            case SqlException sql when sql.Number is 2601 or 2627:
                return true;
            case System.Data.Common.DbException db when db.SqlState is "2601" or "2627" or "23505":
                return true;
            default:
                return false;
        }
    }
}