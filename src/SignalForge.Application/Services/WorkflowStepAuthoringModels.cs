namespace SignalForge.Application.Services;

/// <summary>
/// Command to add a step to a draft workflow version. Step numbering is explicit so the API can
/// insert at a position (existing steps are shifted); when <see cref="StepNumber"/> is null the
/// step is appended at the end of the sequence.
/// </summary>
public sealed record AddStepCommand(
    int? StepNumber,
    string StepType,
    string Configuration,
    string? Name = null,
    string? Description = null,
    bool IsEnabled = true);

/// <summary>
/// Partial update to an existing step. Null fields are left untouched.
/// </summary>
public sealed record UpdateStepCommand(
    string? Name = null,
    string? Description = null,
    string? Configuration = null,
    bool? IsEnabled = null);