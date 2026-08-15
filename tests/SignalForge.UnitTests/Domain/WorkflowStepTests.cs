using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class WorkflowStepTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, "Delay", """{"seconds":5}""", "wait");

        Assert.Equal(2, step.StepNumber);
        Assert.Equal("Delay", step.StepType);
        Assert.Equal("""{"seconds":5}""", step.Configuration);
        Assert.Equal("wait", step.Name);
        Assert.True(step.IsEnabled);
        Assert.NotEqual(default, step.CreatedAt);
        Assert.NotEqual(default, step.UpdatedAt);
    }

    [Fact]
    public void Create_trims_step_type_and_name()
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 1, "  Delay  ", "{}", "  wait  ");

        Assert.Equal("Delay", step.StepType);
        Assert.Equal("wait", step.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Create_rejects_non_positive_step_number(int stepNumber)
    {
        Assert.Throws<ArgumentException>(() =>
            WorkflowStep.Create(Guid.NewGuid(), stepNumber, "Delay", "{}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_step_type(string? stepType)
    {
        Assert.Throws<ArgumentException>(() =>
            WorkflowStep.Create(Guid.NewGuid(), 1, stepType!, "{}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_configuration(string? configuration)
    {
        Assert.Throws<ArgumentException>(() =>
            WorkflowStep.Create(Guid.NewGuid(), 1, "Delay", configuration!));
    }

    [Fact]
    public void UpdateInfo_updates_fields()
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 1, "Delay", """{"seconds":5}""");

        step.UpdateInfo(name: "renamed", configuration: """{"seconds":10}""", isEnabled: false);

        Assert.Equal("renamed", step.Name);
        Assert.Equal("""{"seconds":10}""", step.Configuration);
        Assert.False(step.IsEnabled);
    }

    [Fact]
    public void UpdateInfo_rejects_blank_configuration()
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 1, "Delay", """{"seconds":5}""");

        Assert.Throws<ArgumentException>(() => step.UpdateInfo(configuration: "  "));
    }

    [Fact]
    public void Disable_Enable_toggle_state()
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 1, "Delay", "{}");

        step.Disable();
        Assert.False(step.IsEnabled);

        step.Enable();
        Assert.True(step.IsEnabled);
    }
}