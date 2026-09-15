using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests;

/// <summary>
/// Provider-specific unique-violation detection lives behind the Application's
/// <see cref="IUniqueViolationDetector"/> port. The SQL Server implementation recognizes the
/// SQLSTATE codes EF Core surfaces on the base <c>DbException</c> (2601/2627 index and key,
/// 23505 Postgres) and rejects anything that is not a storage-level unique violation.
/// </summary>
public sealed class SqlUniqueKeyViolationDetectorTests
{
    private sealed class FakeDbException : DbException
    {
        public FakeDbException(string? sqlState, string? message)
            : base(message, innerException: null)
        {
            SqlState = sqlState;
        }

        public override string? SqlState { get; }
    }

    private static DbUpdateException DbUpdate(Exception inner)
        => new("An error occurred while saving the entity changes.", inner);

    [Theory]
    [InlineData("2601")]
    [InlineData("2627")]
    [InlineData("23505")]
    public void Recognizes_unique_violation_sqlstates(string sqlState)
    {
        var detector = new SqlUniqueKeyViolationDetector();
        var ex = DbUpdate(new FakeDbException(sqlState, "Cannot insert duplicate key"));

        Assert.True(detector.IsUniqueViolation(ex));
    }

    [Fact]
    public void DbUpdateException_without_unique_violation_is_rejected()
    {
        var detector = new SqlUniqueKeyViolationDetector();
        var ex = DbUpdate(new FakeDbException("23000", "integrity constraint violation"));

        Assert.False(detector.IsUniqueViolation(ex));
    }

    [Fact]
    public void Plain_exception_is_rejected()
    {
        var detector = new SqlUniqueKeyViolationDetector();

        Assert.False(detector.IsUniqueViolation(new InvalidOperationException("boom")));
        Assert.False(detector.IsUniqueViolation(DbUpdate(new InvalidOperationException("boom"))));
    }
}