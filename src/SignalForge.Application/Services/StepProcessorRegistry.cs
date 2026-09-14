using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalForge.Application.Services;

/// <summary>
/// In-memory registry of step processors. Step-type keys are normalized to lowercase and compared
/// case-insensitively. A null/Duplicate processor key surfaces as an argument error.
/// </summary>
public class StepProcessorRegistry : IStepProcessorRegistry
{
    private readonly IReadOnlyDictionary<string, IStepProcessor> _processors;

    public StepProcessorRegistry(IEnumerable<IStepProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);

        var byType = new Dictionary<string, IStepProcessor>(StringComparer.OrdinalIgnoreCase);
        foreach (var processor in processors)
        {
            ArgumentNullException.ThrowIfNull(processor);
            if (!byType.TryAdd(processor.StepTypeKey, processor))
                throw new InvalidOperationException(
                    $"Step processor type '{processor.StepTypeKey}' registered more than once.");
        }

        _processors = byType;
    }

    public IStepProcessor? Get(string stepType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepType);
        return _processors.GetValueOrDefault(stepType.Trim());
    }
}