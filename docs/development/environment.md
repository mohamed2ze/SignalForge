# Development Environment

## System Information
- **OS**: Ubuntu 26.04
- **.NET SDK**: Version 10.0.111
- **.NET Runtime**: Version 10.0.11
- **Docker**: Version 29.1.3

## SQL Server Configuration
A SQL Server container is already running with the following configuration:
- **Container Name**: `mssql`
- **Image**: `mcr.microsoft.com/mssql/server:latest`
- **Host Port**: 1433 (mapped to container port 1433)
- **SA Password**: set in container environment (do not commit to the repo)
- **Database**: SignalForge (created/applied via EF migrations)
- **Volume**: `mssql_data` persisted to `/var/opt/mssql`

## Connection String

**No connection string is committed to the repository.** The value must be provided per
developer machine via user-secrets (Development) or an environment variable (any environment).

Connection string format for local development:
```
Server=localhost,1433;Database=SignalForge;User Id=sa;Password=<your-sa-password>;TrustServerCertificate=True;
```

### Setting it via user-secrets (local, recommended)

```bash
dotnet user-secrets set --project src/SignalForge.Api "ConnectionStrings:SignalForgeConnection" "<value>"
dotnet user-secrets set --project src/SignalForge.Worker "ConnectionStrings:SignalForgeConnection" "<value>"
```

Stored under `~/.microsoft/usersecrets/` — never in the repo.

### Setting it via environment variable

```bash
export ConnectionStrings__SignalForgeConnection="<value>"   # note the double underscore
```

### Precedence (low → high)

`appsettings.json` → `appsettings.{Environment}.json` → user-secrets (Development only) →
environment variables → command line.

In production/compose, configure the **environment variables** (e.g. `ConnectionStrings__SignalForgeConnection`,
`Seed__DefaultApiKey`). Locally, prefer **user-secrets**. `appsettings*.json` never carry those
values (a CI secret-scan gate enforces this, see Stage 4 L3).

If no connection string is configured, the application starts but fails loudly at first database
access with a clear "connection string" error.

### Worked example — the seeder key

The seeder reads `Seed:DefaultApiKey` (with `Seed:Enabled=true` default). The precedence chain lets
every environment supply its own value without touching the repo:

| Layer | Place to set it | Command / file |
|-------|-----------------|----------------|
| Local (recommended) | user-secrets | `dotnet user-secrets set --project src/SignalForge.Api "Seed:DefaultApiKey" "<key>"` |
| Local/CI without user-secrets | env var | `export Seed__DefaultApiKey="<key>"` |
| Docker Compose | `.env` (gitignored) | `SEED_API_KEY=<key>` in `.env`, referenced by `docker-compose.yml` |
| Container override (any) | environment on the container | `Seed__DefaultApiKey=<key>` in your orchestrator |

If unset everywhere, the seeder generates a **random** key via a cryptographic RNG. Either way the
key is hashed (SHA-256) before storage; the plain text is never persisted in the DB. Generated
credentials are **not** logged by default — set `Seed:ExposeGeneratedSecrets=true` (local dev only)
to print them once at seed time.

## Docker Compose full stack (one-command)

For a reproducible environment, Stage 4 L1 added `docker-compose.yml`. Secrets there come from a
gitignored `.env` file (template: `.env.example`) and are injected as container env vars — nothing
secret is committed:

```bash
cp .env.example .env        # fill in MSSQL_SA_PASSWORD + SEED_API_KEY
docker compose up -d --build
```

- SQL Server: `localhost:14333`, volume `mssql-data`
- API: `localhost:5089`, env overrides: `ConnectionStrings__SignalForgeConnection`,
  `Migrations__AutoApply=true` (migrate at startup), `Seed__TenantName`/`Seed__ApiKeyName`/
  `Seed__DefaultApiKey`
- Worker: same connection string env var.

`.env` is gitignored (see `.gitignore`) — never commit it. Full run/teardown instructions are in
the README's "Docker Compose" section.

## Seeding (default tenant + sample API key)

On startup the API runs an **idempotent seeder** that creates:

- a default tenant (`Id`: `3fa85f64-5717-4562-b3fc-2c963f66afa6` — override via `Seed:TenantId`),
- a sample API key for local development (generated with a cryptographic RNG).

The sample key can be provided explicitly via `Seed:DefaultApiKey` (user-secrets/env); otherwise a
random 32-character key is generated and — like the webhook signing secret — not printed to logs
unless `Seed:ExposeGeneratedSecrets=true`. Re-running never duplicates data.

## Worker configuration

The Worker is a generic-host console app (`Microsoft.NET.Sdk.Worker`). Two notes:

- Run it with `DOTNET_ENVIRONMENT=Development` (not `ASPNETCORE_ENVIRONMENT`) so user-secrets are
  loaded and the launch profile's env vars apply when `--no-launch-profile` is used:
  ```bash
  DOTNET_ENVIRONMENT=Development dotnet run --project src/SignalForge.Worker --no-launch-profile
  ```
- Outbox processing is configurable via the `Outbox` section (or
  `Outbox__*` env vars, e.g. `Outbox__MaxAttempts`):
  - `BatchSize` (100): outbox messages claimed per poll cycle
  - `PollIntervalSeconds` (5): idle/healthy poll delay
  - `MaxAttempts` (5): retries before a message is moved to `DeadLetterMessages`
  - `FailureThreshold` (3): consecutive failing cycles before the poll circuit opens
  - `MaxBackoffSeconds` (300): cap on the exponential backoff between cycles

## Prerequisites
1. .NET 10 SDK installed
2. Docker running with accessible SQL Server container
3. Ubuntu Linux environment

## Notes
- Do not modify or recreate the existing SQL Server container
- The application should use environment variables or user-secrets for connection strings
- TrustServerCertificate is set to True for development convenience (would use proper certificates in production)