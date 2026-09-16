using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SignalForge.Application.Notifications;
using SignalForge.Application.Security;
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
            services.AddScoped<IWorkflowExecutionAdvancer, WorkflowExecutionAdvancer>();
            services.AddScoped<IStepExecutionRetryPolicy, StepExecutionRetryPolicy>();
            services.AddOptions<StepRetryPolicyOptions>();
            services.AddScoped<IWorkflowService, WorkflowService>();
            services.AddScoped<IDeadLetterProcessingService, DeadLetterProcessingService>();
            services.AddScoped<IExecutionObservabilityService, ExecutionObservabilityService>();

            // Ordered step processors by interface (for enumeration/DI) and by concrete type
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

            // Step processors are additionally resolved by step type through this registry
            // (mirrors the notification-provider registry pattern).
            services.AddScoped<IStepProcessorRegistry, StepProcessorRegistry>();

            // Retryable operations: dispatched by the RetryableOperationStepProcessor through the
            // registry. Each registered operation is a real implementation selected by the step's
            // operationType key.
            services.AddSingleton<IRetryableOperationRegistry, RetryableOperationRegistry>();
            services.AddSingleton<IRetryableOperation, EchoRetryableOperation>();

            // Notification providers, resolved by provider type through the registry; the webhook provider
            // uses the typed HttpClientFactory so its handler is mockable in tests. Email is
            // delivered over real SMTP (MailKit); SMS over a real HTTP gateway; both transports
            // fail closed when their options are unconfigured and never log message content.
            services.AddOptions<SmtpNotificationOptions>();
            services.AddOptions<SmsNotificationOptions>();

            services.AddSingleton<IOutboundEmailTransport, MailKitEmailTransport>();
            services.AddHttpClient<IOutboundSmsTransport, HttpSmsTransport>()
                .ConfigureOutboundWebhookDefaults();

            services.AddOptions<OutboundWebhookOptions>();
            services.AddHttpClient<WebhookNotificationProvider>().ConfigureOutboundWebhookDefaults();
            services.AddHttpClient(Options.DefaultName).ConfigureOutboundWebhookDefaults();
            services.AddTransient<INotificationProvider, EmailNotificationProvider>();
            services.AddTransient<INotificationProvider, SmsNotificationProvider>();
            services.AddTransient<INotificationProvider>(sp => sp.GetRequiredService<WebhookNotificationProvider>());
            services.AddScoped<INotificationProviderRegistry, NotificationProviderRegistry>();

            return services;
        }

        /// <summary>
        /// Hardens the outbound webhook HttpClient pipeline: https-only, redirects disabled, an
        /// SSRF guard handler on the request path, and explicit connect/request timeouts. Applied
        /// to the typed webhook provider client and to the anonymous client used by the workflow
        /// webhook step processor.
        /// </summary>
        private static IHttpClientBuilder ConfigureOutboundWebhookDefaults(this IHttpClientBuilder builder)
        {
            return builder
                .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = sp.GetRequiredService<IOptions<OutboundWebhookOptions>>().Value.ConnectTimeout,
                })
                .AddHttpMessageHandler(sp => new OutboundHttpRequestGuardHandler(
                    sp.GetRequiredService<IOptions<OutboundWebhookOptions>>().Value))
                .ConfigureHttpClient((sp, client) =>
                    client.Timeout = sp.GetRequiredService<IOptions<OutboundWebhookOptions>>().Value.Timeout);
        }
    }
}