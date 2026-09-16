using Microsoft.EntityFrameworkCore;
using SignalForge.Application;
using SignalForge.Application.Data;
using SignalForge.Application.Security;
using SignalForge.Application.Services;
using SignalForge.Domain;
using SignalForge.Infrastructure.Data;
using SignalForge.Worker;
using SignalForge.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// Structured JSON console logging for containers/CI, gated behind Logging:Console:Json=true
// (set through the compose env or prod config). Local dev keeps the readable default console.
if (builder.Configuration.GetValue<bool>("Logging:Console:Json", false))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        options.UseUtcTimestamp = true;
    });
}

// Outbox processor options
builder.Services.Configure<OutboxOptions>(
    builder.Configuration.GetSection("Outbox"));

// Shared step retry/backoff policy options (used by the execution pump's advancement path)
builder.Services.Configure<StepRetryPolicyOptions>(
    builder.Configuration.GetSection("StepRetry"));

// Host-level options (graceful shutdown / drain window shared across loops)
builder.Services.Configure<HostingOptions>(
    builder.Configuration.GetSection("Worker"));

// Add database context
builder.Services.AddDbContext<SignalForgeDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("SignalForgeConnection")));

// Register DbContext as the ISignalForgeDbContext implementation
builder.Services.AddScoped<ISignalForgeDbContext, SignalForgeDbContext>();

// Add application services: workflow orchestration + step processors (needed by the execution
// pump's per-cycle scope) and the notification providers.
builder.Services.AddApplicationServices();

// Persistence-context services that live in the Infrastructure layer but back Application ports.
builder.Services.AddScoped<IUniqueViolationDetector, SqlUniqueKeyViolationDetector>();

// Outbound webhook SSRF/timeout/size settings (defaults are strict; override per environment).
builder.Services.AddOptions<OutboundWebhookOptions>()
    .Bind(builder.Configuration.GetSection("OutboundWebhook"));

// Add outbox processing services.
// The processor is a singleton: it owns poll pacing / circuit-breaker state across cycles
// and creates a fresh scope per cycle to resolve scoped services (DbContext, sender).
builder.Services.AddSingleton<IOutboxProcessor, OutboxProcessor>();
builder.Services.AddScoped<IOutboxMessageSender, OutboxMessageSender>();

// Workflow execution pump: advances started workflow executions until they reach a terminal
// state. Singleton + per-cycle scope, paced by its hosted service.
builder.Services.Configure<ExecutionPumpOptions>(
    builder.Configuration.GetSection("ExecutionPump"));
builder.Services.AddSingleton<IWorkflowExecutionPump, WorkflowExecutionPump>();

// Message broker transport (Broker:Provider selects InMemory or durable Sql backend).
builder.Services.AddMessageBroker(builder.Configuration);

// Add hosted services
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<WorkflowExecutionPumpHostedService>();

var host = builder.Build();
host.Run();