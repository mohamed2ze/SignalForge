using System;

namespace SignalForge.Application.Services;

/// <summary>
/// Resolves <see cref="IRetryableOperation"/> implementations by their operation key.
/// </summary>
public interface IRetryableOperationRegistry
{
    /// <summary>Returns the operation registered for <paramref name="operationType"/>, or null.</summary>
    IRetryableOperation? Get(string operationType);
}