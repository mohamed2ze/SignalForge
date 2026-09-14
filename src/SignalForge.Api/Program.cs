using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SignalForge.Api.Health;
using SignalForge.Api.Middleware;
using SignalForge.Application;
using SignalForge.Application.Data;
using SignalForge.Application.Security;
using SignalForge.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

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

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Register API controllers (Events, Workflows, DeadLetter)
builder.Services.AddControllers();

// Health checks (no extra package needed): /health/live = liveness (process serves requests),
// /health/ready = readiness (database reachable). See the MapHealthChecks calls below.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

// Add SignalForge DbContext with SQL Server connection
builder.Services.AddDbContext<SignalForgeDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("SignalForgeConnection"),
        sql =>
        {
            // First-boot SQL Server containers can be slow to finish their setup (tempdb
            // allocation etc.), which surfaces as command/connection timeouts. Treat those
            // (SqlException -2 timeout, Win32 error 258) as retryable in addition to EF's
            // built-in transient-error list.
            sql.CommandTimeout(120);
            sql.EnableRetryOnFailure(
                maxRetryCount: 6,
                maxRetryDelay: TimeSpan.FromSeconds(20),
                errorNumbersToAdd: [-2, 258]);
        }));

// Register DbContext as the ISignalForgeDbContext implementation
builder.Services.AddScoped<ISignalForgeDbContext, SignalForgeDbContext>();

// Add application services
builder.Services.AddApplicationServices();

// Outbound webhook SSRF/timeout/size settings (defaults are strict; override per environment).
builder.Services.AddOptions<OutboundWebhookOptions>()
    .Bind(builder.Configuration.GetSection("OutboundWebhook"));

// Add authentication services
builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.AuthenticationScheme)
    .AddApiKeyAuthentication();

// Authorization lets rate limiting read the api_key_id claim populated by authentication below.
builder.Services.AddAuthorization();

// Options for the API-key-scoped rate limiter (see ApiKeyRateLimitMiddleware). Defaults are a
// generous 1000 req/min per API key; tighten via "RateLimiting:PermitLimit".
builder.Services.Configure<ApiKeyRateLimitOptions>(
    builder.Configuration.GetSection(ApiKeyRateLimitOptions.SectionName));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// HttpsRedirection cannot determine an HTTPS port under a test host (TestServer), so it is
// skipped in the "Testing" environment used by the API integration tests (WebApplicationFactory).
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

// Add authentication middleware
app.UseAuthentication();

// Enrich log output with correlation/tenant/api-key scoped properties for every request.
app.UseMiddleware<LoggingScopeMiddleware>();

// Verify webhook signatures on POST /api/events (hard-required). Runs after auth
// (needs the tenant claim) and before the controllers, so rejection happens pre-DB-write.
app.UseMiddleware<EventsSignatureMiddleware>();

app.UseAuthorization();

// API-key rate limiting reads the api_key_id claim populated by UseAuthentication, so it runs
// after authentication/authorization and before endpoint dispatch. Health probes are exempt.
app.UseMiddleware<ApiKeyRateLimitMiddleware>();

// Route API controllers (Events, Workflows, DeadLetter)
app.MapControllers();

// Liveness: no checks registered (predicate => false), so it only reports whether the API
// process is up and serving. Readiness: runs the database probe and returns 503 while the
// DB is unreachable, so orchestrators/compose can gate traffic on real readiness.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = _ => true,
});

// Apply EF migrations at startup only when explicitly enabled (e.g. the Docker Compose stack,
// where a fresh database must be created before seeding). Local dev keeps using `dotnet ef
// database update`; gate via config/env: Migrations:AutoApply=true. If the database genuinely
// can't be migrated in container mode, fail fast so the container's `restart: on-failure`
// policy retries the whole boot once SQL Server has finished initializing.
if (builder.Configuration.GetValue<bool>("Migrations:AutoApply", false))
{
    try
    {
        // Run migrations on a NON-pooled connection: idle pooled connections hold a shared
        // lock on the freshly created database and permanently block EF's exclusive
        // "ALTER DATABASE SET READ_COMMITTED_SNAPSHOT ON" migration step.
        var migrationConnectionString = new SqlConnectionStringBuilder(
            builder.Configuration.GetConnectionString("SignalForgeConnection"))
        {
            Pooling = false
        }.ConnectionString;

        await using var migrateScope = app.Services.CreateAsyncScope();
        var migrateDbContext = new SignalForgeDbContext(
            new DbContextOptionsBuilder<SignalForgeDbContext>()
                .UseSqlServer(migrationConnectionString, sql => sql.CommandTimeout(120))
                .Options);

        await migrateDbContext.Database.MigrateAsync();
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogInformation("[Migrations] Applied database migrations at startup");
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogError(ex,
                "[Migrations] Startup migration failed; container will restart: {Message}", ex.Message);
        throw;
    }
}

// Seed default tenant and sample API key on first run (idempotent).
// Override values via the "Seed" config section (user-secrets / env vars).
try
{
    using var seederScope = app.Services.CreateScope();
    var dbContext = seederScope.ServiceProvider.GetRequiredService<SignalForgeDbContext>();
    var seederLogger = seederScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var seedSection = app.Configuration.GetSection("Seed");

    if (seedSection.GetValue<bool>("Enabled", true))
    {
        var seed = await DatabaseSeeder.EnsureSeedDataAsync(
            dbContext,
            tenantIdOverride: seedSection.GetValue<Guid?>("TenantId"),
            tenantNameOverride: seedSection.GetValue<string>("TenantName"),
            apiKeyNameOverride: seedSection.GetValue<string>("ApiKeyName"),
            plainTextApiKey: seedSection.GetValue<string>("DefaultApiKey"),
            signingSecretOverride: seedSection.GetValue<string>("SigningSecret"));

        // Default is to NOT print generated credentials: the signing secret is stored in
        // recoverable form for HMAC at request time and must not end up in logs. Set
        // Seed:ExposeGeneratedSecrets=true (local dev only) to echo the sample keys.
        if (seedSection.GetValue<bool>("ExposeGeneratedSecrets", false))
        {
            if (seed.ApiKey is not null || seed.SigningSecret is not null)
            {
                var seederMessage =
                    $"[Seed] Created default tenant {DatabaseSeeder.DefaultTenantId}.";
                if (seed.ApiKey is not null)
                    seederMessage += $" Sample API key for local dev: {seed.ApiKey}";
                if (seed.SigningSecret is not null)
                    seederMessage += $" Webhook signing secret for local dev: {seed.SigningSecret}";
                seederLogger.LogInformation("{Message}", seederMessage);
            }
            else
            {
                seederLogger.LogDebug("[Seed] Default tenant already seeded; nothing to add.");
            }
        }
        else
        {
            if (seed.ApiKey is not null || seed.SigningSecret is not null)
            {
                seederLogger.LogInformation(
                    "[Seed] Created default tenant {TenantId} with generated credentials. " +
                    "Set Seed:ExposeGeneratedSecrets=true to print them (local dev only).",
                    DatabaseSeeder.DefaultTenantId);
            }
            else
            {
                seederLogger.LogDebug("[Seed] Default tenant already seeded; nothing to add.");
            }
        }
    }
}
catch (Exception ex)
{
    // Don't block app startup — DB may be unreachable or not yet migrated.
    var seedFallbackLogger = app.Services.GetRequiredService<ILogger<Program>>();
    seedFallbackLogger.LogWarning(ex,
        "[Seed] Database seeding skipped or failed: {Message}", ex.Message);
}

app.Run();