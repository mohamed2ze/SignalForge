using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalForge.Application.Services;

/// <summary>
/// In-memory registry of retryable operations. Keys are normalized to lowercase and compared
/// case-insensitively; a null/Duplicate operation surfaces as an argument error, mirroring
/// <see cref="StepProcessorRegistry"/>.
/// </summary>
public class RetryableOperationRegistry : IRetryableOperationRegistry
{
    private readonly IReadOnlyDictionary<string, IRetryableOperation> _operations;

    public RetryableOperationRegistry(IEnumerable<IRetryableOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var byType = new Dictionary<string, IRetryableOperation>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (!byType.TryAdd(operation.OperationType, operation))
                throw new InvalidOperationException(
                    $"Retryable operation '{operation.OperationType}' registered more than once.");
        }

        _operations = byType;
    }

    public IRetryableOperation? Get(string operationType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);
        return _operations.GetValueOrDefault(operationType.Trim());
    }
}