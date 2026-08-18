using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests;

public class ConditionalStepProcessorTests
{
    private const int TrueStep = 5;
    private const int FalseStep = 7;

    private static StepExecutionContext ContextWithEventType(string eventType)
    {
        var root = new JsonObject
        {
            ["event"] = new JsonObject { ["type"] = eventType, ["payload"] = new JsonObject() },
            ["output"] = new JsonObject()
        };
        return new StepExecutionContext(root);
    }

    private static (ConditionalStepProcessor Processor, WorkflowStepExecution Execution) Create(string expression)
    {
        string configJson =
            $$"""{"expression":"{{expression}}","trueStep":{{TrueStep}},"falseStep":{{FalseStep}}}""";

        var step = WorkflowStep.Create(Guid.NewGuid(), 3, nameof(StepType.Conditional), configJson);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 1);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        return (new ConditionalStepProcessor(NullLogger<ConditionalStepProcessor>.Instance), execution);
    }

    private static void SetWorkflowStepNavigation(WorkflowStepExecution execution, WorkflowStep step)
    {
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
    }

    [Fact]
    public async Task True_branch_routes_to_trueStep()
    {
        var (processor, execution) = Create("$.event.type == 'order.created'");

        bool shouldContinue = await processor.ProcessAsync(execution, ContextWithEventType("order.created"));

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(TrueStep, execution.RouteToStepNumber);
        Assert.Contains("TRUE", execution.Output);
    }

    [Fact]
    public async Task False_branch_routes_to_falseStep()
    {
        var (processor, execution) = Create("$.event.type == 'order.created'");

        bool shouldContinue = await processor.ProcessAsync(execution, ContextWithEventType("order.cancelled"));

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(FalseStep, execution.RouteToStepNumber);
        Assert.Contains("FALSE", execution.Output);
    }

    [Fact]
    public async Task True_and_false_branches_route_to_different_steps()
    {
        var (trueProcessor, trueExecution) = Create("$.event.type == 'order.created'");
        var (falseProcessor, falseExecution) = Create("$.event.type == 'order.created'");

        await trueProcessor.ProcessAsync(trueExecution, ContextWithEventType("order.created"));
        await falseProcessor.ProcessAsync(falseExecution, ContextWithEventType("order.cancelled"));

        Assert.Equal(TrueStep, trueExecution.RouteToStepNumber);
        Assert.Equal(FalseStep, falseExecution.RouteToStepNumber);
        Assert.NotEqual(trueExecution.RouteToStepNumber, falseExecution.RouteToStepNumber);
    }

    [Fact]
    public async Task Malformed_expression_safely_routes_to_falseStep()
    {
        var (processor, execution) = Create("$.event.type ==");

        bool shouldContinue = await processor.ProcessAsync(execution, ContextWithEventType("order.created"));

        // Log + safe default = evaluate false; step still succeeds and routes to the else branch.
        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(FalseStep, execution.RouteToStepNumber);
    }

    [Fact]
    public async Task Missing_context_routes_to_falseStep_for_path_expression()
    {
        var (processor, execution) = Create("$.event.type == 'order.created'");

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(FalseStep, execution.RouteToStepNumber);
    }

    [Fact]
    public async Task Broken_configuration_fails_the_step()
    {
        var step = WorkflowStep.Create(
            Guid.NewGuid(),
            3,
            nameof(StepType.Conditional),
            """{"trueStep":5,"falseStep":7}""");
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 1);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        var processor = new ConditionalStepProcessor(NullLogger<ConditionalStepProcessor>.Instance);

        bool shouldContinue = await processor.ProcessAsync(execution, ContextWithEventType("order.created"));

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
    }
}