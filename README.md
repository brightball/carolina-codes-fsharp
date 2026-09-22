# carolina-codes-fsharp

Read-only v1 polyglot API for Carolina Code Conference. F# + ASP.NET + Npgsql.

Queries PostgreSQL `v1_*` views. Registers with Elixir once on boot.

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
