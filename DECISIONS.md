# Decisions

ADR-style log for this F# / ASP.NET Core service. Each entry has a status, context, decision, and consequences. Git history is the changelog. Do not delete an accepted entry. Supersede it by changing the status and adding a new entry.

Accepted decisions are binding. A change that revises a durable choice updates this file in the same commit. Operational commands and version pins live in [MEMORY.md](MEMORY.md). Agent instructions live in [AGENTS.md](AGENTS.md).

## ADR-0001: Read-only v1 views

- Status: Accepted
- Date: 2026-08-28

### Context

The Phoenix CMS owns the conference data. Ash resource tables are its private model. Polyglot APIs share one public SQL surface so any language can answer the same routes.

### Decision

This process is read-only. Query only `v1_speakers`, `v1_sponsors`, `v1_years`, `v1_talks`, `v1_sponsorships`, `v1_year_speakers`, and `v1_year_sponsors`. Do not query Ash tables. Do not `SELECT` from base tables such as `speakers`, `organizations`, or `talks`. No writes. No migrations and no catalog SQL in this repo. The views live in the CMS database (Postgres 16).

List routes return `{ "data": [ ... ] }`. Detail routes return `{ "data": { ... } }`. An unknown slug is 404 `{ "error": "not_found" }`.

### Consequences

Schema changes happen in the CMS views. This service has no `db/*.sql`. Payload fields follow `priv/api/openapi.yaml` in the CMS repo. Year-scoped speaker rows carry `languages` and `topics`. Year-scoped sponsor rows carry `tier` and `blurb`.

## ADR-0002: Register once, then keep serving

- Status: Accepted
- Date: 2026-08-28

### Context

`Carolina.Polyglot` keeps at most one language API warm and keep-alives that process. A heartbeat from each sibling would fight that rotation. The API is still useful when the CMS is down.

### Decision

On boot, after the process is listening, POST once to `{CAROLINA_URL}/internal/api-endpoints/register` with `Authorization: Bearer {POLYGLOT_REGISTER_TOKEN}`. The JSON body carries `language` (`F#`), `language_version`, `api_version`, `framework` (`ASP.NET`), `created_year`, `schema_version` (1), `base_url` (`PUBLIC_BASE_URL`), and `endpoints` as the route objects this process already serves (method, path, query names).

Register once. No heartbeat. If `CAROLINA_URL` or the token is empty, or the POST fails, log and continue. Registration does not open Postgres.

### Consequences

A refused connection or an HTTP error must not stop Kestrel. The registered framework string stays `ASP.NET` (the ASP.NET Core 8 shared framework). Do not rename it to `ASP.NET Core`. Do not add a periodic register loop.

## ADR-0003: Lazy catalog open

- Status: Accepted
- Date: 2026-09-22

### Context

Fly checks `GET /health` and expects the process to accept connections even when Postgres is down. Opening the pool during startup ties listen and liveness to the database.

### Decision

Listen first. Do not open the catalog in `main`. `GET /health` returns `{ "ok": true }` and `GET /` returns identity, both with no SQL. The Npgsql data source is created on the first query that needs it. A dead pooled connection is dropped and the query is retried once.

### Consequences

`/health` stays cheap and does not touch the database. The first data request pays for the connection. Handler tests can serve health and identity with no database. Registration must stay off the catalog path.

## ADR-0004: .NET 8, Npgsql, ReadyToRun, and a 512 MB Fly machine

- Status: Accepted
- Date: 2026-09-22

### Context

The process runs on a 512 MB shared-cpu Fly machine that may suspend. Cold start is dominated by JIT and by holding idle database connections. .NET has no JVM CRaC snapshot. IPv6 listen landed with the Fly networking work on 2026-09-01. The runtime pins below landed with the cold-start work on 2026-09-22.

### Decision

- F# on .NET 8. `global.json` pins SDK `8.0.100` with `rollForward` `latestMajor`. Both projects target `net8.0`.
- ASP.NET Core 8 is the shared framework from `Microsoft.NET.Sdk.Web`. The image build uses `mcr.microsoft.com/dotnet/sdk:8.0` and the runtime uses `mcr.microsoft.com/dotnet/aspnet:8.0`. Publish is framework-dependent (`--self-contained false`).
- Database client is Npgsql 8.0.6.
- Release publish sets `PublishReadyToRun`. The image sets `DOTNET_ReadyToRun=1`. ReadyToRun is the cold-start improvement.
- workstation GC (`ServerGarbageCollection` false, `DOTNET_gcServer=0`) with concurrent GC. Invariant globalization. Tiered compilation on. Tiered PGO off.
- Npgsql defaults applied when the DSN omits them: connect timeout 5 seconds, min pool 0 (`Minimum Pool Size` 0), maximum pool size 10.
- Listen on `http://[::]:{PORT}` (IPv6, dual-stack). Do not bind `0.0.0.0` only.
- `fly.toml` uses `auto_stop_machines = "suspend"`, `min_machines_running = 0`, a 512 MB shared CPU, and a `GET /health` check.

### Consequences

A newer SDK may compile the app because roll-forward is `latestMajor`. The target framework stays `net8.0`. ReadyToRun needs the linux-x64 crossgen pack at publish time. An empty pool means the first query connects. Suspend is only valid while the machine stays inside Fly's suspend limits. Do not turn on server GC to "speed up" a 512 MB shared CPU.

## ADR-0005: Fake-catalog handler tests

- Status: Accepted
- Date: 2026-09-22

### Context

The `v1_*` views live in the CMS database. `make test` has to pass with no Postgres. The starter's `tests/test_catalog.py`, compose file, and seed images are a forkable catalog this service does not own.

### Decision

`Carolina.Tests` calls `Program.dispatch` with a fake catalog (`queryHook`). Gate and perf tests read the shipped source and config. `make test` does not start a database.

### Consequences

SQL that the fake hook never sees can still be wrong against a live view. Prove that with a CMS database, not by adding `db/*.sql`, `docker-compose.yml`, `images/`, or `tests/test_catalog.py` here. There is no `src/` tree. The implementation is `Program.fs`.

## ADR-0006: Warnings as errors and the five checks

- Status: Accepted
- Date: 2026-09-22

### Context

C# Roslyn analyzers skip `.fs` files. The fleet still wants one gate for tests, static analysis, dependency audit, secrets, and formatting. A warning that only fails in CI is easy to miss locally.

### Decision

- `Directory.Build.props` sets warnings-as-errors (`TreatWarningsAsErrors`) and warning level 5. Nullable stays disabled so warn-as-error is the same on the SDK 8 image and on newer compilers.
- Tests are xUnit 2.9.2 (`Microsoft.NET.Test.Sdk` 17.11.1, `xunit.runner.visualstudio` 2.8.2).
- Semgrep CE rules in `semgrep.yml` scan F# (`make sast`).
- NuGet audit is on in `Carolina.fsproj`, and `make audit` runs `dotnet list package --vulnerable --include-transitive`.
- `make secrets` runs gitleaks (mise pin 8.30.1).
- Fantomas 7.0.6 (`.config/dotnet-tools.json`) checks formatting (`make lint`).
- `make check` runs tests, Semgrep, NuGet audit, gitleaks, and Fantomas. Pre-commit runs those five checks.

### Consequences

A compiler warning fails `make test`. Format with Fantomas. Semgrep here is a generic matcher over `.fs`, not a full F# analyzer. Do not add a second warnings job. The existing test job is the failure point.
