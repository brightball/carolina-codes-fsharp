# carolina-codes-fsharp

Read-only v1 polyglot API for Carolina Code Conference. F# + ASP.NET + Npgsql.

Queries PostgreSQL `v1_*` views. Registers with Elixir once on boot.

```bash
dotnet restore
DATABASE_URL=postgres://postgres:postgres@127.0.0.1:5432/carolina_dev \
CAROLINA_URL=http://127.0.0.1:4000 \
POLYGLOT_REGISTER_TOKEN=dev \
PUBLIC_BASE_URL=http://127.0.0.1:4010 \
PORT=4010 \
dotnet run
```
