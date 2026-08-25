using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Notifications;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests;

public class NotificationStepProcessorTests
{
    private static INotificationProviderRegistry RegistryWith(params INotificationProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return new NotificationProviderRegistry(providers.Length == 0
            ? [new EmailNotificationProvider(NullLogger<EmailNotificationProvider>.Instance)]
            : providers);
    }

    private static (WorkflowStepExecution Execution, NotificationStepProcessor Processor) Create(
        string config,
        params INotificationProvider[] providers)
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 6, nameof(StepType.NotificationSimulation), config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 6);
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
        execution.Start();

        var processor = new NotificationStepProcessor(
            RegistryWith(providers),
            NullLogger<NotificationStepProcessor>.Instance);

        return (execution, processor);
    }

    [Fact]
    public async Task Email_delivery_succeeds_and_records_delivery_id_on_output()
    {
        var (execution, processor) = Create(
            """{"type":"email","recipient":"user@example.com","subject":"Your order","body":"It shipped"}""",
            new EmailNotificationProvider(NullLogger<EmailNotificationProvider>.Instance));

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);

        using var output = JsonDocument.Parse(execution.Output!);
        Assert.Equal("email", output.RootElement.GetProperty("provider").GetString());
        Assert.Contains("email-", output.RootElement.GetProperty("deliveryId").GetString());
        Assert.Equal("Accepted", output.RootElement.GetProperty("status").GetString());
        Assert.Equal("user@example.com", output.RootElement.GetProperty("recipient").GetString());
    }

    [Fact]
    public async Task Sms_with_null_subject_succeeds()
    {
        var (execution, processor) = Create(
            """{"type":"sms","recipient":"+15551234567","subject":null,"body":"Text only"}""",
            new SmsNotificationProvider(NullLogger<SmsNotificationProvider>.Instance));

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        using var output = JsonDocument.Parse(execution.Output!);
        Assert.Equal("sms", output.RootElement.GetProperty("provider").GetString());
        Assert.Contains("sms-", output.RootElement.GetProperty("deliveryId").GetString());
    }

    [Fact]
    public async Task Explicit_provider_override_wins_over_channel_type()
    {
        var (execution, processor) = Create(
            """{"type":"email","provider":"sms","recipient":"+15551234567","body":"via sms instead"}""",
            new SmsNotificationProvider(NullLogger<SmsNotificationProvider>.Instance));

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        using var output = JsonDocument.Parse(execution.Output!);
        Assert.Equal("sms", output.RootElement.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Unknown_provider_fails_loudly_not_silently()
    {
        var (execution, processor) = Create(
            """{"type":"push","recipient":"dev@example.com","body":"unsupported channel"}""",
            new EmailNotificationProvider(NullLogger<EmailNotificationProvider>.Instance));

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("push", execution.ErrorMessage);
        Assert.Contains("Available providers", execution.ErrorMessage);
        Assert.Contains("email", execution.ErrorMessage);
    }

    [Fact]
    public async Task Missing_recipient_throws()
    {
        var (execution, processor) = Create(
            """{"type":"email","body":"no recipient"}""",
            new EmailNotificationProvider(NullLogger<EmailNotificationProvider>.Instance));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => processor.ProcessAsync(execution, null));
    }

    [Fact]
    public async Task Cancellation_cancels_the_step()
    {
        var (execution, processor) = Create(
            """{"type":"email","recipient":"user@example.com","body":"cancelled"}""",
            new EmailNotificationProvider(NullLogger<EmailNotificationProvider>.Instance));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool shouldContinue = await processor.ProcessAsync(execution, null, cts.Token);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Cancelled, execution.Status);
    }

    [Fact]
    public async Task Webhook_delivery_uses_mocked_handler_and_keeps_provider_delivery_id()
    {
        using var handler = new FixedHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Headers = { { "X-Delivery-Id", "wh-abc-123" } }
        });
        using var client = new HttpClient(handler);
        var webhook = new WebhookNotificationProvider(client, NullLogger<WebhookNotificationProvider>.Instance);

        var (execution, processor) = Create(
            """{"type":"webhook","provider":"webhook","recipient":"https://echo.example.com/hook","body":"payload"}""",
            webhook);

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        using var output = JsonDocument.Parse(execution.Output!);
        Assert.Equal("webhook", output.RootElement.GetProperty("provider").GetString());
        Assert.Equal("wh-abc-123", output.RootElement.GetProperty("deliveryId").GetString());
    }

    [Fact]
    public async Task Webhook_rejection_fails_the_step_with_status()
    {
        using var handler = new FixedHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream exploded")
        });
        using var client = new HttpClient(handler);
        var webhook = new WebhookNotificationProvider(client, NullLogger<WebhookNotificationProvider>.Instance);

        var (execution, processor) = Create(
            """{"type":"webhook","recipient":"https://echo.example.com/hook","body":"payload"}""",
            webhook);

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("502", execution.ErrorMessage);
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