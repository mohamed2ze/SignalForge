# Remove EF Core from `SignalForge.Application` — migration plan

Status: **plan** (not implemented). Tracking: `CODE_REVIEW.md §6 item 5`.

## Objective

Make `SignalForge.Application.csproj` build without a reference to `Microsoft.EntityFrameworkCore`, so the
application layer depends only on the domain and on contracts it owns (a "true port" in hexagonal terms).
Entity Framework Core must remain, but confined to `SignalForge.Infrastructure` (the adapter), plus the
already-EF `SignalForge.Worker` and `SignalForge.Api` entry points, which legitimately talk to the store.

## Why this is risky (and why we plan, not execute)

EF is not incidental here. The application services currently lean on three EF provider features that have
no non-EF equivalent:

1. **Deferred `IQueryable` LINQ** is used for multi-table joins, correlated subqueries (tenant-active
   filter in the pumps), group-by aggregations (`ExecutionObservabilityService`), and paged enumerations
   (`DeadLetterProcessingService`, `WorkflowService`). Replacing that with a port of method calls forces a
   large, query-by-query API surface.
2. **The change tracker** drives the unit of work: `SaveChangesAsync`, `Entry(...)`, navigation fix-up
   (`EventEmissionStepProcessor` relies on fix-up after insert), and shadow-state-free graph updates.
3. **Provider exceptions carry semantics**: `DbUpdateConcurrencyException` (race-proof retry scheduling)
   and unique-key violations (`IUniqueViolationDetector`) are caught in application code today.

Any plan must preserve these three capabilities behind EF-free contracts without silently losing the
race-proofing, atomicity, and aggregation semantics they underpin.

## Current coupling inventory (evidence)

`SignalForge.Application.csproj` references `Microsoft.EntityFrameworkCore` (10.0.11). EF types are used in:

| File | EF surface today | Why it is hard to port |
|---|---|---|
| `Data/ISignalForgeDbContext.cs` | The port interface itself: exposes `DbSet<T>` for every entity, `DatabaseFacade`, `Entry(...)`, `SaveChangesAsync` | Designed as "the DbContext minus infrastructure", so it leaks EF types into Application by construction |
| `Services/ApiKeyValidationService.cs` | 3-table join + `Entry(...)` state fix-up + `DbUpdateConcurrencyException` handling | Join on ApiKey→Tenant→SigningSetting is purpose-built to avoid a second round-trip |
| `Services/WorkflowService.cs` (21 query sites) | Heaviest LINQ consumer: workflow CRUD, versioning, reordering, tenant scoping | Mostly CRUD + validation; the renumber/reorder transactions need a unit of work |
| `Services/DeadLetterProcessingService.cs` (17) | Paged lists, counts, replay requeue (writes outbox rows), mark-processed | Replay is a small command; lists are read models |
| `Services/WorkflowExecutionAdvancer.cs` (15) | Step-state mutations, retry resolution, navigation fix-up, dead-letter writes | State machine with atomic write per transition |
| `Services/WorkflowExecutionOrchestratorService.cs` (9) | Start execution, manual retry scheduling, concurrency-token catch, pump query helper | Concurrency semantics must survive the boundary |
| `Services/ExecutionObservabilityService.cs` (8) | Group-by/cap aggregation into dashboard DTOs | Aggregation is EF-specific today (LINQ to SQL); in-memory reprojection would break the SQL-window semantics |
| `Services/EventIngestionService.cs` (4) | Idempotent insert + unique-violation handling | Already partially abstracted via `IUniqueViolationDetector`; must complete |
| `Services/StepExecutionRetryPolicy.cs` (2) | Retry scheduling + dead-letter creation, concurrency token | Command surface, port-friendly |
| `Services/OutboxPublisher.cs` (1) | Outbox insert within the caller's UoW (`SaveChangesAsync` removed from it in the race-proofing work) | Now nearly a pure command — easiest port |

Already-abstracted, good precedent: `Data/IUniqueViolationDetector.cs` lives in Application, has no EF type,
and is implemented by `Infrastructure/Data/SqlUniqueKeyViolationDetector.cs`. `Broker/`, `RateLimiting/IRateLimiter`
(backed by `Infrastructure/RateLimiting/SqlRateLimiter`), notifications, and webhook security follow the same
port/adapter shape. This plan extends that pattern to persistence.

Direct EF consumers that are **already outside** Application and stay there: `Infrastructure/Data/SignalForgeDbContext.cs`
(plus entity configs and all migrations), `Infrastructure/Data/SqlUniqueKeyViolationDetector.cs`,
`Infrastructure/Broker/SqlMessageBroker.cs`, `Infrastructure/RateLimiting/SqlRateLimiter.cs`,
`Worker/Services/WorkflowExecutionPump.cs` and `OutboxProcessor.cs` (atomic claim + requeue via `ExecuteUpdateAsync`).
Rules of engagement: **Worker and Api keep EF**; the plan only moves what lives in Application.

## Target architecture

```
SignalForge.Application (EF-free)
  ├── Ports (interfaces the app owns & depends on):
  │     IUnitOfWork (commit on command paths)
  │     ITenantStore, IApiKeyStore, IEventStore, IWorkflowStore,
  │     IWorkflowExecutionStore, IOutboxStore, IDeadLetterStore,
  │     IObservabilityStore, ISigningSettingStore
  │     IUniqueViolationDetector (exists — keep, it is the model)
  │   Services (business logic only; composition root injects ports)
  └── SignalForge.Domain (unchanged)
SignalForge.Infrastructure (adapter)
  └── SignalForgeDbContext + entity configs + migrations       (unchanged)
  └── Persistence/* — EF implementations of every port:
        SqlUnitOfWork, Sql*Store (repository/query methods in SQL or LINQ),
        Existing: SqlUniqueKeyViolationDetector, SqlMessageBroker, SqlRateLimiter
SignalForge.Worker (EF stays) — pumps keep their own DbContext usage
```

Design decisions:

- **Commands are methods, not tracker mutations.** Methods that today mutate tracked entities and call
  `SaveChangesAsync` in Application (step transitions, retries, replay, renumber) become named commands on a
  port; the adapter runs them in one `ExecuteUpdateAsync`/change-tracking round trip. This preserves atomicity
  and lets the adapter own `SaveChangesAsync`.
- **Concurrency is expressed in the domain, not as `DbException`.** The adapter catches
  `DbUpdateConcurrencyException` and maps it to a domain result (`Conflict` / `StaleData`) the Application can
  branch on (the same shape as today's `NotEligible` retry path), so Application code no longer names EF
  exceptions.
- **Read models are projections.** Observation/dashboard/list queries move to ports returning DTOs
  (`IObservabilityStore`, `IDeadLetterStore.GetPageAsync`, …). Application never materializes raw entity graphs
  for reporting; this removes `IQueryable` composition from Application.
- **`ISignalForgeDbContext` is deleted**, replaced by the port interfaces above. Nothing in Application is
  allowed to mention `DbSet`, `EntityEntry`, or `DatabaseFacade`.

## Phased work with files touched

Each phase ends with a green build, all unit + integration tests passing, coverage ≥ 80%, format clean.

### Phase 0 — baseline & frozen decision record
- Add a preflight job that asserts `SignalForge.Application.csproj` has no EF package reference later in the
  CI matrix (a one-line grep in a workflow step). Files: `.github/workflows/*`.

### Phase 1 — the easy commands (low risk, high value)
Ports where the current Application method is already a small, self-contained write:
- `OutboxPublisher` (`IOutboxStore.EnqueueAsync` on the caller's unit of work; `OutboxPublisher` keeps a single
  `IUnitOfWork` argument instead of the whole context).
- `EventIngestionService` (`IEventStore.InsertIfUniqueAsync` + existing `IUniqueViolationDetector`).
- `DeadLetterProcessingService` replay + mark-processed (`IDeadLetterStore.RequeueAsync`, `MarkProcessedAsync`).
- `ApiKeyValidationService` lookup (`IApiKeyStore.FindActiveByPrefixAsync` returning a small value object;
  the usage-stamp / rehash update becomes one command; concurrency-loss tolerance moves into the adapter).
- `StepExecutionRetryPolicy` (`IWorkflowExecutionStore.ScheduleStepRetryAsync`, `FailedStepExhaustionAsync`).
Files: `Application/Data/*` (new ports), `Application/Services/{OutboxPublisher,EventIngestionService,
DeadLetterProcessingService,ApiKeyValidationService,StepExecutionRetryPolicy}.cs`, new `Infrastructure/Persistence/*`.

### Phase 2 — the workflow store (medium risk)
`WorkflowService` CRUD/versioning/reorder, `WorkflowExecutionOrchestratorService` start/cancel:
- `IWorkflowStore` covers create/draft/publish/enable/disable/version/snapshot, steps CRUD, reorder
  (`IRepositionStepsAsync`)? — command; tenant scoping always inside the adapter.
- `IWorkflowExecutionStore.StartAsync` creates + starts + saves in one adapter call; the tenant-active guard
  becomes a store-level precondition that returns a domain result (`TenantDisabled`) instead of throwing inside
  Application.
- Pump candidate selection logic moves to a store port (`IWorkflowExecutionStore.GetClaimableBatchAsync`) so
  the worker's fairness/batching math can stay in Worker while tenant filtering executes in Infrastructure.
Files: `Application/Services/WorkflowService.cs`, `Application/Services/WorkflowExecutionOrchestratorService.cs`,
new `Infrastructure/Persistence/SqlWorkflowStore.cs`, `SqlWorkflowExecutionStore.cs`, `SqlUnitOfWork.cs`.

### Phase 3 — the state machine (high risk; the reason we plan)
`WorkflowExecutionAdvancer` + step processors that write:
- Step-execution lifecycle becomes `IWorkflowExecutionStore.TransitionStepAsync(next)`, executed by the adapter
  under one commit with the dead-letter creation (`IDeadLetterStore.CreateFromFailedStep`), preserving the
  atomic outbox+step-success emit introduced earlier.
- `EventEmissionStepProcessor` navigation fix-up dependency is removed by having the adapter return the created
  object needing the "step→execution" link, or the processor receives ids and relies on store joins — no tracker
  fix-up.
Files: `Application/Services/WorkflowExecutionAdvancer.cs`, `Application/Services/EventEmissionStepProcessor.cs`,
`WorkflowStepExecution`/`Event` adapters.

### Phase 4 — observability reads (medium risk)
`ExecutionObservabilityService` aggregations: move group-by/cap SQL to `IObservabilityStore.GetDashboardsAsync`
returning the DTOs. Keep the value-sink idiom the integration tests rely on (`EF`-free — the store returns exact
aggregates). Files: `Application/Services/ExecutionObservabilityService.cs`,
`Infrastructure/Persistence/SqlObservabilityStore.cs`.

### Phase 5 — decouple the interface, cut the package
- Delete `Application/Data/ISignalForgeDbContext.cs`; delete the per-service context constructor injections.
- Rewire `ServiceCollectionExtensions` to register the new stores (`AddScoped<IWorkflowStore, SqlWorkflowStore>`,
  etc.) while keeping `AddApplicationServices` EF-free.
- Remove the `Microsoft.EntityFrameworkCore` PackageReference from `SignalForge.Application.csproj`.
- Composition roots (`Infrastructure` DI, `Worker`/`Api` Program.cs) register `SignalForgeDbContext` only, plus
  the adapters.
- Update `ServiceCollectionExtensions`/`Api`/`Worker` registration lines, `tests/SignalForge.UnitTests/Services/*`
  constructor calls, and `BuildScopeFactory` helpers (unit tests swap the context for a memory-backed adapter).

### Phase 6 — hold the line
- `rg "Microsoft.EntityFrameworkCore|ISignalForgeDbContext|DbContext" src/SignalForge.Application` must return
  nothing.
- Migrations, `SignalForgeDbContext`, and the startup migration runner stay in Infrastructure/Api untouched.

## What stays

- Every migration under `Infrastructure/Migrations` and `SignalForgeDbContext` + entity configurations.
- `Infrastructure/Data/DatabaseSeeder`, `SqlMessageBroker`, `SqlRateLimiter`, `SqlUniqueKeyViolationDetector`.
- The worker pumps (`WorkflowExecutionPump`, `OutboxProcessor`) and their atomic claim/requeue SQL helper paths.
- The domain model and all existing EF-free contracts already inside Application.

## Test & release strategy

Per-phase gates (all must be green before merging a phase):
1. `dotnet build SignalForge.slnx` — 0 warnings, 0 errors.
2. `dotnet test tests/SignalForge.UnitTests` — full suite, no `--filter` narrowing (449 tests).
3. `dotnet test tests/SignalForge.IntegrationTests -c Release` — 70 tests against the real SQL container:
   the concurrency-race, multi-worker, replay, and multi-tenant tests are the regression tripwire for phases 2–4.
4. `./.github/scripts/enforce-coverage.sh 80` — line coverage stays ≥ 80%.
5. `dotnet format SignalForge.slnx --verify-no-changes`.

Rollback: each phase is an independent commit; a regression in phase N reverts that commit only. Phases 1–3
are behavioral clones with the same public service contracts, so controllers/endpoints and the Worker are
untouched until phase 5 rewires DI (the only point requiring a coordinated deploy).

Release sequencing: ship phases 1–4 (behavior-identical, ports behind the scenes), then phase 5 (package
removal + DI rewiring) as a single commit, then phase 6.

## Risks & mitigations

- **Race semantics regression**: the concurrency-token catch in `ScheduleStepRetryAsync` must map through the
  adapter. Mitigation: stages 1–3 keep an adapter unit test throwing `DbUpdateConcurrencyException` and asserting
  the domain `Conflict` result (the existing `ConcurrencyConflictRetryPolicy` test shape).
- **Aggregation drift**: observability/dead-letter pagination must match today's SQL line for line.
  Mitigation: the SQL-window integration tests already pin exact counts and slice sizes.
- **UoW split**: services that currently share one tracked context (advancer reads + writes step graph) need the
  store to encapsulate the whole transition, not just the write. Mitigation: phase 3 is deliberately last among
  behavior changes and is covered by `WorkflowExecutionPumpTests`/pump concurrency tests.
- **Test-fixture churn**: unit tests construct `SignalForgeDbContext` directly; after phase 5 they switch to an
  in-memory `IUnitOfWork`/store fakes, so the fixture layer changes in one commit, not per-phase.

## Effort

Rough sizing by phase (subject to change during execution): P0 <0.5d, P1 1–2d, P2 1–2d, P3 2–4d, P4 1–2d,
P5 1d, P6 0.5d. Net ~1 week of focused work plus fixture rewiring, done serially to preserve the gate tripwires.