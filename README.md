# carolina-codes-fsharp

Read-only v1 polyglot API for Carolina Code Conference. F# + ASP.NET + Npgsql.

Queries PostgreSQL `v1_*` views. Registers with Elixir once on boot.

## Versions

- Language: F# on .NET 8. `global.json` pins SDK `8.0.100` with `rollForward` `latestMajor`. Target framework `net8.0`.
- Framework: ASP.NET Core 8, the shared framework from `Microsoft.NET.Sdk.Web` and `mcr.microsoft.com/dotnet/aspnet:8.0`. The build image is `mcr.microsoft.com/dotnet/sdk:8.0`. The registered identity string is `ASP.NET`.
- Database client: Npgsql 8.0.6.
- Tests: xUnit 2.9.2, Microsoft.NET.Test.Sdk 17.11.1, xunit.runner.visualstudio 2.8.2.
- Formatting: Fantomas 7.0.6 (`.config/dotnet-tools.json`, `make lint`).
- Secrets scan: gitleaks 8.30.1 (mise).
- Cold start: ReadyToRun (`PublishReadyToRun` on Release publish). That is the cold-start improvement for this runtime. This service does not use JVM CRaC.

```bash
make test        # xUnit via dotnet test (no Postgres for handler cases)
make sast        # Semgrep CE scan of F# source
make audit       # NuGet --vulnerable --include-transitive
make secrets     # gitleaks detect --source .
make lint        # Fantomas --check
make check       # all of the above
make hooks       # install local pre-commit hooks
```

Pre-commit runs the same five checks (`local tests`, `static security scanner`, `3rd-party dependency scanner`, `gitleaks`, `fantomas`). Install once with `make hooks` (needs `pre-commit`, `semgrep`, `gitleaks`, and `dotnet` on PATH). Emergency skip: `SKIP=local-tests,sast,audit,gitleaks,style git commit`.

```bash
dotnet restore
DATABASE_URL=postgres://postgres:postgres@127.0.0.1:5432/carolina_dev \
CAROLINA_URL=http://127.0.0.1:4000 \
POLYGLOT_REGISTER_TOKEN=dev \
PUBLIC_BASE_URL=http://127.0.0.1:4010 \
PORT=4010 \
dotnet run
```
