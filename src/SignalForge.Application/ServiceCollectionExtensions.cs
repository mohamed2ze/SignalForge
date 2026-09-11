using Microsoft.Extensions.DependencyInjection;
using SignalForge.Application.Notifications;
using SignalForge.Application.Services;

namespace SignalForge.Application
{
    /// <summary>
    /// Extension methods for registering application services.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds application services to the service collection.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddApplicationServices(this IServiceCollection services)
        {
            // Register other application services first (to avoid circular dependency issues during validation)
            services.AddScoped<IApiKeyValidationService, ApiKeyValidationService>();
            services.AddScoped<IOutboxPublisher, OutboxPublisher>();
            services.AddScoped<IEventIngestionService, EventIngestionService>();
            services.AddScoped<IWorkflowExecutionOrchestratorService, WorkflowExecutionOrchestratorService>();
            services.AddScoped<IWorkflowService, WorkflowService>();
            services.AddScoped<IDeadLetterProcessingService, DeadLetterProcessingService>();
            services.AddScoped<IExecutionObservabilityService, ExecutionObservabilityService>();

            // Register step processors by interface (for enumeration/DI) and by concrete type
            // (the orchestrator resolves processors by concrete type via GetStepProcessorForType).
            services.AddScoped<HttpWebhookStepProcessor>();
            services.AddScoped<DelayStepProcessor>();
            services.AddScoped<ConditionalStepProcessor>();
            services.AddScoped<LogAuditStepProcessor>();
            services.AddScoped<NotificationStepProcessor>();
            services.AddScoped<EventEmissionStepProcessor>();
            services.AddScoped<RetryableOperationStepProcessor>();

            services.AddScoped<IStepProcessor, HttpWebhookStepProcessor>();
            services.AddScoped<IStepProcessor, DelayStepProcessor>();
            services.AddScoped<IStepProcessor, ConditionalStepProcessor>();
            services.AddScoped<IStepProcessor, LogAuditStepProcessor>();
            services.AddScoped<IStepProcessor, NotificationStepProcessor>();
            services.AddScoped<IStepProcessor, EventEmissionStepProcessor>();
            services.AddScoped<IStepProcessor, RetryableOperationStepProcessor>();

            // Notification providers, resolved by provider type through the registry;
            // the webhook provider uses the typed HttpClientFactory so its handler is mockable in
            // tests. Swap the simulated email/sms registrations for real adapters later.
            services.AddHttpClient<WebhookNotificationProvider>();
            services.AddTransient<INotificationProvider, EmailNotificationProvider>();
            services.AddTransient<INotificationProvider, SmsNotificationProvider>();
            services.AddTransient<INotificationProvider>(sp => sp.GetRequiredService<WebhookNotificationProvider>());
            services.AddScoped<INotificationProviderRegistry, NotificationProviderRegistry>();

            return services;
        }
    }
}