# SignalForge

**SignalForge** is an event-driven **workflow orchestration platform**. Tenants define, version,
and execute workflows that are triggered by incoming events. Workflows are composed of sequential
steps — HTTP webhooks, delays, conditionals, audit logging, notifications, event emission, and
retryable operations — with multi-tenancy, API-key authentication, a transactional outbox for
reliable messaging, exponential-backoff retries, and a dead-letter queue.

> **Status:** early-stage prototype. The engine is demonstrable end to end; the cloud message
> broker is in-memory (a durable transport is a config-selectable swap-in), while webhook
> delivery, the transactional outbox, workflow execution, and email/SMS notification transports
> (SMTP + HTTP SMS gateway, fail-closed when unconfigured) are real implementations.

---

## Architecture

Clean Architecture with strict layer separation, DDD-style domain models, and strongly typed IDs.

```
SignalForge.Domain          Entities, value objects, enums — no dependencies
SignalForge.Application     Business services, step processors, orchestrator, ingestion, validation
SignalForge.Infrastructure  EF Core DbContext, entity mappings, migrations
SignalForge.Api             ASP.NET Core Web API — events, workflows, dead-letter endpoints
SignalForge.Worker          Background worker — polls the outbox, publishes pending messages,
                            and advances running workflow executions (execution pump)
tests/UnitTests             xunit — domain, security, service, and provider units
tests/IntegrationTests      xunit — full-stack against SQL Server: auth, ingestion
                             idempotency, outbox/dead-letter, rate limiting, isolation
```

Data flow at a high level:

```
Client ──HTTP event──▶ API (EventsController, X-API-Key auth)
                         │ ingest (idempotent by TenantId + ExternalEventId)
                         ▼
                     DB ── outbox message written transactionally ──▶ Worker polls outbox
                                                                         │ publish
Worker ──▶ workflow execution (sequential steps, retries, dead-letter on exhaustion)
```

Execution orchestration is split across three focused services in `SignalForge.Application`:
`WorkflowExecutionOrchestratorService` owns the lifecycle (start, read, retry eligibility),
`WorkflowExecutionAdvancer` owns step advancement (the "what runs next" decision, dispatched
through the step-processor registry), and `StepExecutionRetryPolicy` owns the shared
retry/backoff/dead-letter decision used by both the worker and the HTTP retry endpoint.

---

## Prerequisites

- **.NET 10 SDK** (includes runtime and EF CLI via local tools)
- **Docker** with a reachable **SQL Server** container (see `docs/development/environment.md`)

---

## Setup

```bash
# 1. Restore NuGet packages and local tools
dotnet restore SignalForge.slnx
dotnet tool restore

# 2. Start SQL Server (follow docs/development/environment.md; a container on localhost:1433
#    named `mssql` is the documented dev setup)

# 3. Configure the connection string
#    No connection string is committed. Set it via user-secrets (Development) or env vars:
dotnet user-secrets set --project src/SignalForge.Api "ConnectionStrings:SignalForgeConnection" "<value>"
dotnet user-secrets set --project src/SignalForge.Worker "ConnectionStrings:SignalForgeConnection" "<value>"
#    (format: Server=localhost,1433;Database=SignalForge;User Id=sa;Password=<pwd>;TrustServerCertificate=True;
#     the compose stack instead connects as the least-privilege signalforge_app login — see RUNNING.md

# 4. Apply the database migration
dotnet ef database update --project src/SignalForge.Infrastructure --startup-project src/SignalForge.Api
```

---

## Running

**API host** (`http://localhost:5xxx` — see `src/SignalForge.Api/Properties/launchSettings.json`):

```bash
dotnet run --project src/SignalForge.Api
```

**Worker (outbox publisher + execution pump):**

```bash
dotnet run --project src/SignalForge.Worker
```

The worker shuts down gracefully: on stop it stops accepting new outbox batches/execution
advancement cycles immediately, but gives an in-flight cycle up to `Worker:GracefulShutdownTimeoutSeconds`
(default 30) to finish its current work before it is hard-cancelled, so a restart never interrupts
a send/advance mid-request.

**Tests:**

```bash
dotnet test SignalForge.slnx
```

**Continuous-Integration gates** (`.github/workflows/ci.yml`) enforce, on every push/PR:

- no committed secrets (secret-scan job),
- a clean `dotnet build -warnaserror` and `dotnet format --verify-no-changes`,
- the full unit + integration test suites,
- a coverage gate: the integration suite runs with the XPlat collector (`codecoverage.runsettings`)
  and `.github/scripts/enforce-coverage.sh` fails the pipeline if line coverage drops below **80%**.

OpenAPI docs are available in Development at `/openapi/v1.json`.

---

## Running the full stack with Docker Compose

One command brings up SQL Server 2022, the API, and the outbox worker from clean:

```bash
# 1. Provide secrets (copy once, fill in your values — .env is gitignored)
cp .env.example .env
#    edit MSSQL_SA_PASSWORD, APP_DB_PASSWORD, SEED_API_KEY, SEED_SIGNING_SECRET as needed

# 2. Start the stack
docker compose up -d --build
```

- SQL Server listens on `localhost:14333` (kept off 1433 to avoid clashing with a local dev DB);
  data persists in the `mssql-data` volume.
- API listens on `http://localhost:5089`, seeded with the key from `SEED_API_KEY` in `.env`:

```bash
curl -H "X-API-Key: <SEED_API_KEY from .env>" http://localhost:5089/api/workflows   # -> HTTP 200 []
```

- `Migrations:AutoApply=true` is set for the stack env, so the API creates/migrates the
  `SignalForge` database at startup before seeding. Local dev (non-container) keeps using
  `dotnet ef database update` — the auto-apply flag defaults to **off**.
- Logs: `docker compose logs -f api worker`.
- Health endpoints: `http://localhost:5089/health/live` (liveness — 200 whenever the API process
  is up, DB not required) and `http://localhost:5089/health/ready` (readiness — 200 with a
  reachable database, 503 otherwise). Docker marks the API container healthy via the ready probe;
  the worker starts only after that gate.
- Metrics: `http://localhost:5089/metrics` exposes Prometheus-formatted metrics — built-in HTTP
  request metrics, .NET runtime metrics, and SignalForge gauges/counters (outbox pending, active
  executions, dead-letter backlog, events ingested, rate-limit rejections). Scrape it from the
  monitoring network only; disable via `Metrics:Enabled=false`.
- Teardown: `docker compose down` (add `-v` to also drop the `mssql-data` volume).

---

## Authentication

The API is protected by **API-key authentication**:

- Header: `X-API-Key: <key>`
- Keys are stored as **versioned PBKDF2-SHA256 hashes** (random salt, constant-time compare in
  `ApiKeyValidationService`); legacy unversioned hashes upgrade transparently on first login.
- The tenant is resolved from the authenticated key and injected into identity claims
  (`tenant_id`, `api_key_id`); every entity is tenant-scoped.
- Each request is additionally checked against a **per-API-key rate limit**
  (`ApiKeyRateLimitMiddleware`). Default is 1000 requests/min per key, override via
  `RateLimiting:PermitLimit`; the counter store is configurable via `RateLimiting:Store` —
  `InMemory` (default, per process) or `Sql` (a single budget across every API node, using the
  shared `RateLimitCounters` table).

On first startup the API **seeds** a default tenant and a sample API key (idempotent). Generated
credentials are **not** printed by default (the webhook signing secret must never reach logs). For
local dev, set `Seed:ExposeGeneratedSecrets=true` to print them once at seed time. Re-running
never duplicates them.

---

## Workflow Steps

| Step type | Behavior |
|-----------|----------|
| `HttpWebhook` | Makes an HTTP request to a configured endpoint (https-only, SSRF-guarded, timeouts enforced) |
| `Delay` | Waits for a configured duration |
| `Conditional` | Evaluates a JSON-path condition expression against the execution context and routes to the true/false branch (`false` when evaluation errors) |
| `LogAudit` | Writes an audit log entry |
| `NotificationSimulation` | Sends a notification via the provider registry (webhook delivers real HTTP; email goes through SMTP via MailKit; SMS through the configured gateway — both fail closed when unconfigured) |
| `EventEmission` | Emits a new event back into the platform |
| `RetryableOperation` | Wraps an operation with retry logic (exponential backoff; the operation type is dispatched to a registered `IRetryableOperation` implementation; an unregistered type fails deterministically) |

Steps are authored through the step-management API against a draft version: add
(`POST .../versions/{versionId}/steps`), update (`PUT .../steps/{stepId}`), reorder
(`PUT .../steps/reorder`), enable/disable (`POST .../steps/{stepId}/enable|disable`), and delete
(`DELETE .../steps/{stepId}`); publish the version before it is executable.

Failed steps can be retried on demand:
`POST /api/workflows/{workflowId}/executions/{executionId}/steps/{stepExecutionId}/retry` (409 while
the step is still running, waiting, or has exhausted its attempts).

---

## Documentation

- `docs/development/environment.md` — dev environment and SQL Server setup
- `docs/development/event-signing.md` — webhook event signing scheme (HMAC-SHA256) and client example
