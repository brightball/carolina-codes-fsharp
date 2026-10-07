# carolina-codes-fsharp

Read-only v1 polyglot HTTP API. F# on ASP.NET Core 8. Install, run, and test commands are in [README.md](README.md). Version pins and remotes are in [MEMORY.md](MEMORY.md). Accepted decisions are in [DECISIONS.md](DECISIONS.md).

This file keeps the Carolina polyglot starter contract (v1 SQL views, required routes, register-once) and the layout this repository actually has.

## Contract

The source of truth for routes and payloads is the CMS repository (`github.com/brightball/carolina-codes`): `priv/api/openapi.yaml` and `priv/api/AGENTS.md`. This tree has no `openapi.yaml`. A public-contract change belongs in those CMS files.

Do not implement Ash JSON:API (`application/vnd.api+json`). This API speaks ordinary JSON over the v1 REST + SQL-view contract.

This repository is its own git remote (`github.com/brightball/carolina-codes-fsharp`). Treat this repo as the workspace root. The Phoenix CMS is a different remote. Do not assume `../elixir` or other sibling directories exist. Do not fold this tree into the CMS git remote.

Handler tests do not need a CMS checkout. Registration is best-effort. If `CAROLINA_URL` is unset or the CMS is down, log and continue, and still serve HTTP.

## Purpose

The Phoenix app (`Carolina.Polyglot`) keeps **at most one** language API warm and reads speakers and sponsors from it. With no APIs registered, it falls back to Ash. This process must:

1. Query PostgreSQL **v1 views** only. Never query Ash resource tables or base tables.
2. Expose the routes below. The CMS OpenAPI is authoritative for payload fields.
3. **Register once on boot** with the Elixir site (no heartbeat). If the site is not running, log and continue.

## Environment

| Variable | Example | Role |
| --- | --- | --- |
| `DATABASE_URL` | `postgres://postgres:postgres@127.0.0.1:5432/carolina_dev` | SQL views |
| `CAROLINA_URL` | `http://127.0.0.1:4000` | Elixir site (optional; register no-ops if down) |
| `POLYGLOT_REGISTER_TOKEN` | `dev` | Bearer token for register |
| `PUBLIC_BASE_URL` | `http://127.0.0.1:4010` | URL Elixir will call |
| `PORT` | `4010` | Listen port. The container and Fly use `8080` |

Postgres `v1_*` views live in the CMS database (Postgres 16). This repo does not ship that database.

Handler tests use a fake catalog and do not need Postgres. For live HTTP against the views, start Postgres 16 and set the variables above.

## SQL views (query these)

`v1_speakers`, `v1_sponsors`, `v1_years`, `v1_talks`, `v1_sponsorships`, `v1_year_speakers`, `v1_year_sponsors`.

Year-scoped speaker rows include `languages` and `topics` from `v1_talks`. Year-scoped sponsor rows include `tier` and `blurb`.

Do not `SELECT` from `speakers`, `organizations`, `talks`, or other base tables. Do not query Ash tables. The views are the API. There are no writes.

## Required HTTP routes

Wrap list payloads as `{ "data": [ ... ] }`. Detail payloads use `{ "data": { ... } }`. An unknown slug returns 404 `{ "error": "not_found" }`.

- `GET /health` — cheap liveness. Body is `{ "ok": true }`. It does not touch the database.
- `GET /` — identity (`language`, `language_version`, `api_version`, `framework`, `created_year`, `schema_version`, `endpoints`). The `framework` string is `ASP.NET`.
- `GET /v1/years`
- `GET /v1/speakers` and `GET /v1/speakers?year=2025`
- `GET /v1/speakers/{slug}` and `GET /v1/speakers/{year}/{slug}`
- `GET /v1/sponsors` and `GET /v1/sponsors?year=2025`
- `GET /v1/sponsors/{slug}` and `GET /v1/sponsors/{year}/{slug}`

`photo_path` and `logo_path` are web paths. Return the path. This service does not serve image bytes. The CMS hosts the files.

Only `GET` and `HEAD` are served. Any other method returns 405.

## Register on boot (once)

`POST {CAROLINA_URL}/internal/api-endpoints/register`

```
Authorization: Bearer {POLYGLOT_REGISTER_TOKEN}
Content-Type: application/json
```

Body fields: `language`, `language_version`, `api_version`, `framework`, `created_year`, `base_url` (`PUBLIC_BASE_URL`), `schema_version` (1), `endpoints` (the route list this process already serves, including method, path, and query names).

Register once, after the process is listening. No heartbeat. Elixir keep-alives the currently warm API.

If `CAROLINA_URL` or `POLYGLOT_REGISTER_TOKEN` is empty, or the POST fails (connection refused, 4xx/5xx), **log and continue**. Keep serving. Registration does not open Postgres.

## Layout

| Path | Role |
| --- | --- |
| `Program.fs` | Routes, v1 SQL, register-once |
| `Carolina.fsproj` | `Microsoft.NET.Sdk.Web`, `net8.0`, Npgsql |
| `Carolina.sln` | App and test projects |
| `Carolina.Tests/` | xUnit. Fake catalog. No Postgres |
| `global.json` | SDK pin and roll forward |
| `Directory.Build.props` | Warnings as errors |
| `Dockerfile` | `sdk:8.0` build, `aspnet:8.0` runtime, ReadyToRun |
| `fly.toml` | Fly service (suspend, 512 MB, `GET /health`) |
| `Makefile` | `test`, `sast`, `audit`, `secrets`, `lint`, `check`, `hooks` |
| `semgrep.yml` | Semgrep CE rules for `*.fs` |
| `.config/dotnet-tools.json` | Fantomas |
| `.githooks/pre-commit` | Local hooks |
| `README.md` | Install, run, test, versions |
| `MEMORY.md` | Operational memory for agents |
| `DECISIONS.md` | Accepted decisions |

This tree does not contain `openapi.yaml`, `src/`, `db/*.sql`, `images/`, `docker-compose.yml`, or `tests/test_catalog.py`.

## Commands

`make test`, `make sast`, `make audit`, `make secrets`, `make lint`, `make check`, `make hooks`. A local process is `dotnet restore` then `dotnet run`. Pins and the dev env example are in [README.md](README.md) and [MEMORY.md](MEMORY.md).

## Decisions and memory

Read [DECISIONS.md](DECISIONS.md) before changing SQL, registration, hosting, the runtime pin, or how tests run. Accepted decisions are binding and must be updated when a durable choice changes. When a durable choice changes, update `DECISIONS.md` in the same change. Each entry has status, context, decision, and consequences. Git history is the changelog.

[MEMORY.md](MEMORY.md) is short operational memory: commands, pins, the contract pointer, and remotes. It is not a second copy of the decision log. Update it when a pin, command, or remote changes.

For this F# and ASP.NET Core repository, keep that split. One `DECISIONS.md` at the repo root is the record. Supersede a decision by changing its status and leaving the old entry in place.

## Checklist

- Contract paths return 200 with the JSON shape above (404 on an unknown slug)
- `?year=` speaker rows include `languages` and `topics`; year sponsor rows include `tier`
- Register runs once at process start, with no heartbeat, and logs and continues if the Elixir site is down
- No writes. No Ash table names. No base-table reads
- `GET /health` is cheap and does not touch the database
- Handler tests use the fake catalog and pass without Postgres
