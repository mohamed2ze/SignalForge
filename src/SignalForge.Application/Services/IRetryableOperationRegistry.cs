using System;

namespace SignalForge.Application.Services;

public interface IRetryableOperationRegistry
{
    IRetryableOperation? Get(string operationType);
}