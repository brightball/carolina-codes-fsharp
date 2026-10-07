# Memory

Operational facts for agents in this F# / ASP.NET Core repo. Rationale lives in [DECISIONS.md](DECISIONS.md). Those accepted decisions are binding. When a durable choice changes, update `DECISIONS.md` in the same change. Update this file when a command, pin, contract pointer, or remote changes. This file is not a second copy of the decision log.

## What this is

Read-only v1 polyglot API in `Program.fs`. It queries CMS `v1_*` views and registers once on boot. `framework` reported to the CMS is `ASP.NET`.

## Contract

CMS repository: https://github.com/brightball/carolina-codes

- `priv/api/openapi.yaml`
- `priv/api/AGENTS.md`

This tree has no `openapi.yaml`. Query v1 views only. Never query Ash tables or base tables. Instructions for routes and tests are in [AGENTS.md](AGENTS.md).

## Remotes

- `origin`: https://github.com/brightball/carolina-codes-fsharp.git
- `gitea`: `codes-carolina/carolina-codes-fsharp`
- CMS: https://github.com/brightball/carolina-codes

This repo is the workspace root. Do not assume `../elixir` exists. Do not fold this tree into the CMS remote.

## Pins

- SDK `8.0.100` in `global.json`, `rollForward` `latestMajor`, TFM `net8.0`
- `Microsoft.NET.Sdk.Web`
- Build image `mcr.microsoft.com/dotnet/sdk:8.0`
- Runtime image `mcr.microsoft.com/dotnet/aspnet:8.0` (ASP.NET Core 8 shared framework)
- Npgsql 8.0.6
- xUnit 2.9.2, Microsoft.NET.Test.Sdk 17.11.1, xunit.runner.visualstudio 2.8.2
- Fantomas 7.0.6 (`.config/dotnet-tools.json`)
- gitleaks 8.30.1 (mise)
- ReadyToRun: `PublishReadyToRun` on Release. Cold-start path for this runtime. Not JVM CRaC.
- Local default `PORT` is `4010`. Container and Fly use `8080`.

## Commands

```bash
make test        # xUnit via dotnet test (no Postgres for handler cases)
make sast        # Semgrep CE
make audit       # NuGet --vulnerable --include-transitive
make secrets     # gitleaks
make lint        # Fantomas --check
make check       # all of the above
make hooks       # install local pre-commit hooks
dotnet restore
dotnet run
```

Handler tests use a fake catalog and do not need Postgres.

## Dev env

```bash
DATABASE_URL=postgres://postgres:postgres@127.0.0.1:5432/carolina_dev \
CAROLINA_URL=http://127.0.0.1:4000 \
POLYGLOT_REGISTER_TOKEN=dev \
PUBLIC_BASE_URL=http://127.0.0.1:4010 \
PORT=4010 \
dotnet run
```

`CAROLINA_URL` is optional. If it is unset or the CMS is down, register logs and continues.
