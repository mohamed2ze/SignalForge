namespace SignalForge.Application.Data;

/// <summary>
/// Decouples persistence-exception interpretation from the Application layer. Implementations know
/// how their storage provider reports unique-constraint violations (SQL Server error codes 2601/2627,
/// Postgres SQLSTATE 23505) without the Application referencing any EF Core or ADO.NET type.
/// </summary>
public interface IUniqueViolationDetector
{
    /// <summary>
    /// Returns true when <paramref name="exception"/> (or its inner chain) was caused by a
    /// storage-level unique index/constraint violation.
    /// </summary>
    bool IsUniqueViolation(Exception exception);
}