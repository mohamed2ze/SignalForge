using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Notifications;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <summary>
/// Processes notification workflow steps by routing them through an <see cref="INotificationProvider"/>
/// selected by the step's configuration. The delivery outcome (provider, deliveryId, status,
/// recipient, sentAt) is recorded on the step execution output as structured JSON so it is
/// observable through the execution detail endpoints. Unknown provider types fail the step loudly
/// — delivery is never silently skipped.
/// </summary>
public class NotificationStepProcessor : IStepProcessor
{
    public string StepTypeKey => nameof(StepType.NotificationSimulation);

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly INotificationProviderRegistry _registry;
    private readonly ILogger<NotificationStepProcessor> _logger;

    public NotificationStepProcessor(
        INotificationProviderRegistry registry,
        ILogger<NotificationStepProcessor> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ProcessAsync(
        WorkflowStepExecution stepExecution,
        StepExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        // Configuration schema: provider (optional; defaults to the channel type for backward
        // compatibility), recipient, subject (optional), body. "type" is kept as the channel field.
        var config = JsonDocument.Parse(stepExecution.WorkflowStep.Configuration).RootElement;

        var channelType = config.TryGetProperty("type", out var typeProp)
            ? typeProp.GetString()
            : null;
        var provider = config.TryGetProperty("provider", out var providerProp) && providerProp.ValueKind == JsonValueKind.String
            ? providerProp.GetString()
            : channelType;

        var recipient = config.GetProperty("recipient").GetString()!;
        var subject = config.TryGetProperty("subject", out var subjectProp) && subjectProp.ValueKind == JsonValueKind.String
            ? subjectProp.GetString()
            : null;
        var body = config.GetProperty("body").GetString()!;

        var resolved = _registry.Get(provider!);
        if (resolved is null)
        {
            var message = $"Notification provider '{provider}' is not configured. Available providers: " +
                          string.Join(", ", _registry.AvailableProviders);
            _logger.LogWarning("Notification step {StepId} failed: {ErrorMsg}", stepExecution.Id, message);
            stepExecution.Fail(message);
            return false;
        }

        try
        {
            _logger.LogInformation(
                "Delivering {Provider} notification for step {StepId} to {Recipient}",
                resolved.ProviderType, stepExecution.Id, recipient);

            var result = await resolved.SendAsync(
                new NotificationMessage(resolved.ProviderType, recipient, subject, body),
                cancellationToken);

            _logger.LogInformation(
                "Notification {Provider} for step {StepId} accepted with deliveryId {deliveryId}",
                result.ProviderType, stepExecution.Id, result.DeliveryId);

            var output = JsonSerializer.Serialize(new
            {
                provider = result.ProviderType,
                deliveryId = result.DeliveryId,
                status = result.Status.ToString(),
                recipient,
                subject,
                sentAt = result.SentAt
            }, OutputOptions);

            stepExecution.Succeed(output);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Notification step {StepId} was cancelled", stepExecution.Id);
            stepExecution.Cancel();
            return false;
        }
        catch (NotificationDeliveryException ex)
        {
            _logger.LogError(ex, "Notification step {StepId} delivery failed", stepExecution.Id);
            stepExecution.Fail($"Notification delivery failed: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification step {StepId} failed", stepExecution.Id);
            stepExecution.Fail($"Notification step failed: {ex.Message}");
            return false;
        }
    }
}