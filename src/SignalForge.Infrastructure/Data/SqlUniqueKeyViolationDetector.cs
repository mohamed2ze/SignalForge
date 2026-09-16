using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;

namespace SignalForge.Infrastructure.Data;

/// <summary>
/// SQL Server unique-constraint violation detection for <see cref="IUniqueViolationDetector"/>.
/// SQL Server reports these via <see cref="SqlException.Number"/> on the exception that EF Core seeds
/// into <see cref="DbUpdateException.InnerException"/>: 2601 (unique index "Cannot insert duplicate
/// key row") / 2627 (primary-key constraint). The SQLSTATE column on the base
/// <see cref="System.Data.Common.DbException"/> is left empty by Microsoft.Data.SqlClient, so the
/// number — not SqlState — is authoritative for SQL Server. Postgres reports them on
/// <see cref="System.Data.Common.DbException.SqlState"/> as 23505.
/// </summary>
public sealed class SqlUniqueKeyViolationDetector : IUniqueViolationDetector
{
    public bool IsUniqueViolation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            DbUpdateException dbUpdate => dbUpdate.InnerException switch
            {
                SqlException sql when sql.Number is 2601 or 2627 => true,
                System.Data.Common.DbException db when db.SqlState is "2601" or "2627" or "23505" => true,
                _ => false
            },
            _ => false
        };
    }
}