using System.Collections.Generic;

namespace SignalForge.Application.Services;

/// <summary>
/// Resolves step processors by their step-type key.
/// </summary>
public interface IStepProcessorRegistry
{
    /// <summary>
    /// Resolves a processor for a step type, case-insensitively. Returns null when the step type is
    /// not registered — callers must fail loudly rather than silently skip step processing.
    /// </summary>
    IStepProcessor? Get(string stepType);
}