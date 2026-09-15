using SignalForge.Application.Validation;

namespace SignalForge.UnitTests;

public class WorkflowStepConfigurationValidatorTests
{
    [Theory]
    [InlineData("HttpWebhook", """{"url":"https://example.com/hook"}""")]
    [InlineData("Delay", """{"seconds":5}""")]
    [InlineData("Conditional", """{"expression":"$.event.type == 'a'"}""")]
    [InlineData("LogAudit", """{"message":"hello"}""")]
    [InlineData("NotificationSimulation", """{"recipient":"user@example.com"}""")]
    [InlineData("EventEmission", """{"eventType":"order.created"}""")]
    [InlineData("RetryableOperation", """{"operationType":"echo"}""")]
    public void Valid_config_for_known_step_type_passes(string stepType, string config)
    {
        Assert.Null(WorkflowStepConfigurationValidator.Validate(stepType, config));
    }

    [Theory]
    [InlineData("Delay", """{}""", "seconds")]
    [InlineData("HttpWebhook", """{}""", "url")]
    [InlineData("Conditional", """{"trueStep":3}""", "expression")]
    [InlineData("LogAudit", """{"logLevel":"error"}""", "message")]
    [InlineData("NotificationSimulation", """{"subject":"hi"}""", "recipient")]
    [InlineData("EventEmission", """{"payload":"{}"}""", "eventType")]
    [InlineData("RetryableOperation", """{"parameters":{}}""", "operationType")]
    public void Config_missing_required_field_is_rejected(string stepType, string config, string field)
    {
        var error = WorkflowStepConfigurationValidator.Validate(stepType, config);

        Assert.NotNull(error);
        Assert.Contains(field, error);
    }

    [Theory]
    [InlineData("Delay", """{"seconds":"not-a-number"}""")]
    [InlineData("Delay", """{"seconds":-3}""")]
    public void Non_numeric_or_negative_number_field_is_rejected(string stepType, string config)
    {
        Assert.NotNull(WorkflowStepConfigurationValidator.Validate(stepType, config));
    }

    [Fact]
    public void Unknown_step_type_is_rejected()
    {
        var error = WorkflowStepConfigurationValidator.Validate("MysteryStep", "{}");

        Assert.NotNull(error);
        Assert.Contains("Unknown step type", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    public void Blank_or_malformed_config_is_rejected(string config)
    {
        Assert.NotNull(WorkflowStepConfigurationValidator.Validate("Delay", config));
    }

    [Fact]
    public void Step_type_matching_is_case_insensitive()
    {
        Assert.Null(WorkflowStepConfigurationValidator.Validate("delay", """{"seconds":1}"""));
    }
}