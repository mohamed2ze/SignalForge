# Running SignalForge

Two supported ways to run the platform. Option A is the recommended one-command flow; Option B
runs the API and worker natively against your own SQL Server container.

## Option A — Docker Compose (full stack, one command)

Everything (SQL Server 2022 + API + outbox worker) in one stack. The API auto-migrates and seeds
the database at startup. A `db-init` one-shot service (gated before the API/worker) runs the
idempotent bootstrap (`mssql/init/01-create-app-login.sh`), which creates a least-privilege
`signalforge_app` login (db_owner of the `SignalForge` DB); the API/worker connect with it — the
SA login is only used by that bootstrap and the healthcheck.

```bash
# 1. Provide secrets once (template is committed; .env is gitignored)
cp .env.example .env
#    -> edit MSSQL_SA_PASSWORD, APP_DB_PASSWORD, SEED_API_KEY, SEED_SIGNING_SECRET as needed

# 2. Start the stack
docker compose up -d --build

# 3. Wait until the API is healthy (~1-2 min on first boot for SQL Server)
docker compose ps
docker compose logs -f api worker
```

- API: `http://localhost:5089`
- SQL Server: `localhost:14333` (kept off 1433 to avoid clashing with a local dev DB)
- Health: `http://localhost:5089/health/live` and `http://localhost:5089/health/ready`

Test it:

```bash
curl -H "X-API-Key: <SEED_API_KEY from .env>" http://localhost:5089/api/workflows   # -> HTTP 200 []
```

Stop:

```bash
docker compose down          # add -v to also drop the DB volume
```

## Option B — Local .NET against a SQL Server container

Requires the .NET 10 SDK and a reachable SQL Server container on `localhost:1433`
(e.g. `docker start mssql`, template in `docs/development/environment.md`).

### One-time setup

```bash
# 1. Restore NuGet packages and local tools
dotnet restore SignalForge.slnx
dotnet tool restore

# 2. Start SQL Server and confirm it accepts connections
docker start mssql

# 3. Connection strings + seed values (user-secrets, never committed)
dotnet user-secrets set --project src/SignalForge.Api "ConnectionStrings:SignalForgeConnection" "Server=localhost,1433;Database=SignalForge;User Id=sa;Password=<your-sa-password>;TrustServerCertificate=True"
dotnet user-secrets set --project src/SignalForge.Worker "ConnectionStrings:SignalForgeConnection" "<same-connection-string>"
dotnet user-secrets set --project src/SignalForge.Api "Seed:DefaultApiKey" "<your-dev-key>"
dotnet user-secrets set --project src/SignalForge.Api "Seed:SigningSecret" "<your-signing-secret>"

# 4. Apply the database migration
dotnet ef database update --project src/SignalForge.Infrastructure --startup-project src/SignalForge.Api
```

### Run (two terminals)

```bash
# Terminal 1 — API host (http://localhost:5127)
dotnet run --project src/SignalForge.Api

# Terminal 2 — outbox worker (needs DOTNET_ENVIRONMENT, not ASPNETCORE_ENVIRONMENT)
DOTNET_ENVIRONMENT=Development dotnet run --project src/SignalForge.Worker --no-launch-profile
```

### Test

```bash
curl -H "X-API-Key: <your-dev-key>" http://localhost:5127/api/workflows   # -> HTTP 200 []
curl http://localhost:5127/health/ready                                    # -> 200 with a reachable DB
```

## Try it end to end

The event-ingestion path requires a signed request (see `docs/development/event-signing.md`):

```bash
export SF_API_KEY="<api key>"
export SF_SIGNING_SECRET="<tenant signing secret>"

BODY='{"externalEventId":"order-001","eventType":"order.created","payload":"{\"orderId\":42}"}'
TS=$(date +%s)
SIG="sha256=$(printf '%s:%s' "$TS" "$BODY" | openssl dgst -sha256 -hmac "$SF_SIGNING_SECRET" | awk '{print $2}')"

curl -i -X POST http://localhost:5127/api/events \
  -H "X-API-Key: $SF_API_KEY" \
  -H "Content-Type: application/json" \
  -H "X-SignalForge-Timestamp: $TS" \
  -H "X-SignalForge-Signature: $SIG" \
  --data-binary "$BODY"
```

There is no step-management API yet, so a workflow with real steps needs the steps inserted into
`WorkflowSteps` for the version directly (or via `AddStep` in the application layer). After that:
publish the version, create the execution via `POST /api/workflows/{id}/execute`, and the worker's
execution pump advances it step by step (watch with `docker compose logs -f worker` or the local
worker console). A failed step can be rescheduled on demand with
`POST /api/workflows/{id}/executions/{executionId}/steps/{stepExecutionId}/retry` (409 while it is
still running, waiting, or exhausted).

## Known gotchas

- **Least-privilege login on pre-existing volumes.** The `db-init` bootstrap is idempotent and runs
  on every `docker compose up`, so an existing compose volume gains `signalforge_app` automatically
  on the next up. Because the API waits for `db-init` (`service_completed_successfully`), there is
  no startup race between the bootstrap and the app's first connection.
- **Stale seeded API key.** The seeder is idempotent and keeps an existing key, so a new
  `Seed:DefaultApiKey` is ignored on a database that was already seeded. Either wipe the database
  (`docker compose down -v`) or point the stored hash at your key.
- **`EventsSignatureMiddleware` DI.** The middleware must take `ISignalForgeDbContext` as an
  `InvokeAsync` parameter, NOT in its constructor — constructor injection resolves from the root
  provider and fails for scoped services (fixed in `src/SignalForge.Api/Middleware/EventsSignatureMiddleware.cs`).