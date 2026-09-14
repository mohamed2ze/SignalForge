using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Security;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests;

public class HttpWebhookStepProcessorTests
{
    private const string Config =
        """{"url":"https://example.com/webhook","method":"POST","headers":{"X-Trace":"abc"},"body":{"a":1}}""";

    private static HttpWebhookStepProcessor CreateProcessor(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var client = new HttpClient(new FixedHandler(handler));
        return new HttpWebhookStepProcessor(client, NullLogger<HttpWebhookStepProcessor>.Instance);
    }

    private static (WorkflowStepExecution Execution, HttpWebhookStepProcessor Processor) Create(string config)
    {
        var processor = CreateProcessor(request =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") });

        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        return (execution, processor);
    }

    private static void SetWorkflowStepNavigation(WorkflowStepExecution execution, WorkflowStep step)
    {
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
    }

    [Fact]
    public async Task Success_2xx_continues_and_stores_body()
    {
        HttpRequestMessage? captured = null;
        var processor = CreateProcessor(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true}""") };
        });
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), Config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal("""{"ok":true}""", execution.Output);
        Assert.Equal("https://example.com/webhook", captured?.RequestUri?.ToString());
        Assert.True(captured?.Headers.TryGetValues("X-Trace", out var values) == true && values.Contains("abc"));
    }

    [Fact]
    public async Task Response_body_larger_than_the_old_2000_char_column_is_stored_in_full()
    {
        var bigBody = new string('y', 3000);
        var processor = CreateProcessor(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bigBody) });
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), Config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(bigBody, execution.Output);
    }

    [Fact]
    public async Task Response_body_exceeding_the_storage_cap_is_truncated_for_persistence()
    {
        var hugeBody = new string('z', StorageText.DefaultMaxLength + 100);
        var processor = CreateProcessor(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hugeBody) });
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), Config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(StorageText.DefaultMaxLength, execution.Output?.Length);
    }

    [Fact]
    public async Task Non_2xx_fails_the_step()
    {
        var processor = CreateProcessor(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), Config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("500", execution.ErrorMessage);
        Assert.Contains("boom", execution.ErrorMessage);
    }

    [Fact]
    public async Task Timeout_fails_the_step()
    {
        var processor = CreateProcessor(_ => throw new TaskCanceledException());
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), Config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("timed out", execution.ErrorMessage);
    }

    [Fact]
    public async Task Network_error_fails_the_step()
    {
        var processor = CreateProcessor(_ =>
            throw new HttpRequestException(
                HttpRequestError.ConnectionError,
                "no route",
                new SocketException((int)SocketError.HostNotFound)));
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), Config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("Network error", execution.ErrorMessage);
    }

    [Fact]
    public async Task Unexpected_transport_exception_fails_the_step()
    {
        var processor = CreateProcessor(_ => throw new InvalidOperationException("bogus handler"));
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), Config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("Unexpected error", execution.ErrorMessage);
    }

    [Fact]
    public async Task Malformed_configuration_throws()
    {
        var (execution, processor) = Create("""{"url":"https://example.com/webhook"}""");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => processor.ProcessAsync(execution, null));
    }

    [Theory]
    [InlineData("""{"url":"http://example.com/webhook","method":"POST","headers":{},"body":{}}""")]
    [InlineData("""{"url":"https://127.0.0.1/webhook","method":"POST","headers":{},"body":{}}""")]
    [InlineData("""{"url":"https://localhost/webhook","method":"POST","headers":{},"body":{}}""")]
    [InlineData("""{"url":"https://10.0.0.2/webhook","method":"POST","headers":{},"body":{}}""")]
    public async Task Ssrf_target_url_throws_before_any_http_call(string config)
    {
        var processor = CreateProcessor(_ => throw new InvalidOperationException("handler must not be reached"));
        var step = WorkflowStep.Create(Guid.NewGuid(), 2, nameof(StepType.HttpWebhook), config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 2);
        SetWorkflowStepNavigation(execution, step);
        execution.Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(execution, null));

        Assert.DoesNotContain("must not be reached", ex.Message);
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FixedHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}