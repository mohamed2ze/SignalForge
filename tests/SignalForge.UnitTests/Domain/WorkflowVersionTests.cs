using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class WorkflowVersionTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var version = WorkflowVersion.Create(Guid.NewGuid(), 1);

        Assert.Equal(1, version.VersionNumber);
        Assert.False(version.IsPublished);
        Assert.Null(version.PublishedAt);
        Assert.Empty(version.Steps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_non_positive_version_number(int versionNumber)
    {
        Assert.Throws<ArgumentException>(() => WorkflowVersion.Create(Guid.NewGuid(), versionNumber));
    }

    [Fact]
    public void Create_trims_description()
    {
        var version = WorkflowVersion.Create(Guid.NewGuid(), 1, "  desc  ");

        Assert.Equal("desc", version.Description);
    }

    [Fact]
    public void Publish_flips_state_and_sets_PublishedAt()
    {
        var version = WorkflowVersion.Create(Guid.NewGuid(), 1);
        version.AddStep(1, nameof(StepType.Delay), """{"seconds":5}""");

        version.Publish();

        Assert.True(version.IsPublished);
        Assert.NotNull(version.PublishedAt);
    }

    [Fact]
    public void Publish_rejects_version_with_no_steps()
    {
        var version = WorkflowVersion.Create(Guid.NewGuid(), 1);

        Assert.Throws<InvalidOperationException>(() => version.Publish());
        Assert.False(version.IsPublished);
        Assert.Null(version.PublishedAt);
    }

    [Fact]
    public void Unpublish_resets_state()
    {
        var version = WorkflowVersion.Create(Guid.NewGuid(), 1);
        version.AddStep(1, nameof(StepType.Delay), """{"seconds":5}""");
        version.Publish();

        version.Unpublish();

        Assert.False(version.IsPublished);
        Assert.Null(version.PublishedAt);
    }

    [Fact]
    public void AddStep_creates_and_attaches_step()
    {
        var version = WorkflowVersion.Create(Guid.NewGuid(), 1);

        var step = version.AddStep(1, nameof(StepType.Delay), """{"seconds":5}""", "wait");

        Assert.Single(version.Steps);
        Assert.Same(step, version.Steps.First());
        Assert.Equal(1, step.StepNumber);
        Assert.Equal(nameof(StepType.Delay), step.StepType);
        Assert.Equal("wait", step.Name);
    }

    [Fact]
    public void AddStep_rejects_non_positive_step_number()
    {
        var version = WorkflowVersion.Create(Guid.NewGuid(), 1);

        Assert.Throws<ArgumentException>(() =>
            version.AddStep(0, nameof(StepType.Delay), """{"seconds":5}"""));
    }

    [Fact]
    public void UpdateDescription_trims_and_touches_UpdatedAt()
    {
        var version = WorkflowVersion.Create(Guid.NewGuid(), 1);

        version.UpdateDescription("  updated  ");

        Assert.Equal("updated", version.Description);
    }
}