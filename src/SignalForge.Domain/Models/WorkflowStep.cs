namespace SignalForge.Domain.Models;

/// <summary>
/// Represents a step within a workflow version.
/// Different step types implement different behaviors.
/// </summary>
public class WorkflowStep
{
    public Guid Id { get; private set; }
    public Guid WorkflowVersionId { get; private set; }
    public int StepNumber { get; private set; } // Order within the workflow
    public string StepType { get; private set; } = default!; // Type of step (e.g., "HttpWebhook", "Delay")
    public string? Name { get; private set; } // Human-readable name
    public string? Description { get; private set; }
    public string Configuration { get; private set; } = default!; // JSON configuration for the step
    public bool IsEnabled { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; } // For concurrency tracking

    // Navigation properties
    public WorkflowVersion WorkflowVersion { get; private set; } = default!;
    public ICollection<WorkflowStepExecution> StepExecutions { get; private set; } = new List<WorkflowStepExecution>();

    private WorkflowStep() { } // For EF Core

    private WorkflowStep(
        Guid id,
        Guid workflowVersionId,
        int stepNumber,
        string stepType,
        string configuration,
        string? name = null,
        string? description = null,
        bool isEnabled = true)
    {
        Id = id;
        WorkflowVersionId = workflowVersionId;
        StepNumber = stepNumber;
        StepType = stepType ?? throw new ArgumentNullException(nameof(stepType));
        Name = name;
        Description = description?.Trim();
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        IsEnabled = isEnabled;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new workflow step.
    /// </summary>
    /// <param name="workflowVersionId">The workflow version ID</param>
    /// <param name="stepNumber">The step number (order)</param>
    /// <param name="stepType">The type of step</param>
    /// <param name="configuration">JSON configuration for the step</param>
    /// <param name="name">Optional human-readable name</param>
    /// <param name="description">Optional description</param>
    /// <param name="isEnabled">Whether the step is enabled</param>
    /// <returns>A new WorkflowStep instance</returns>
    public static WorkflowStep Create(
        Guid workflowVersionId,
        int stepNumber,
        string stepType,
        string configuration,
        string? name = null,
        string? description = null,
        bool isEnabled = true)
    {
        if (stepNumber <= 0)
            throw new ArgumentException("Step number must be positive", nameof(stepNumber));

        if (string.IsNullOrWhiteSpace(stepType))
            throw new ArgumentException("Step type cannot be empty", nameof(stepType));

        if (string.IsNullOrWhiteSpace(configuration))
            throw new ArgumentException("Step configuration cannot be empty", nameof(configuration));

        return new WorkflowStep(
            Guid.NewGuid(),
            workflowVersionId,
            stepNumber,
            stepType.Trim(),
            configuration,
            name?.Trim(),
            description,
            isEnabled);
    }

    /// <summary>
    /// Updates the step information.
    /// </summary>
    /// <param name="name">The new step name</param>
    /// <param name="description">The new description</param>
    /// <param name="configuration">The new configuration</param>
    /// <param name="isEnabled">Whether the step is enabled</param>
    public void UpdateInfo(
        string? name = null,
        string? description = null,
        string? configuration = null,
        bool? isEnabled = null)
    {
        if (name != null)
            Name = name.Trim();

        if (description != null)
            Description = description.Trim();

        if (configuration != null)
        {
            if (string.IsNullOrWhiteSpace(configuration))
                throw new ArgumentException("Step configuration cannot be empty", nameof(configuration));
            Configuration = configuration;
        }

        if (isEnabled.HasValue)
            IsEnabled = isEnabled.Value;

        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Disables the step.
    /// </summary>
    public void Disable()
    {
        IsEnabled = false;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Enables the step.
    /// </summary>
    public void Enable()
    {
        IsEnabled = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Moves the step to a new position in the version's sequence.
    /// </summary>
    /// <param name="stepNumber">The new 1-based step number</param>
    public void MoveTo(int stepNumber)
    {
        if (stepNumber <= 0)
            throw new ArgumentException("Step number must be positive", nameof(stepNumber));

        StepNumber = stepNumber;
        UpdatedAt = DateTime.UtcNow;
    }
}