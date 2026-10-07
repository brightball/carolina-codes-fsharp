module DocTests

open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit

let text name =
    File.ReadAllText(TestPaths.findFile name)

let capture pattern source =
    let found = Regex.Match(source, pattern)
    Assert.True(found.Success, sprintf "pattern not in source: %s" pattern)
    found.Groups[1].Value

let require (docName: string) (doc: string) (needles: string list) =
    let absent = needles |> List.filter (fun needle -> not (doc.Contains needle))

    Assert.True(List.isEmpty absent, sprintf "%s missing: %s" docName (String.concat ", " absent))

let headingCount (heading: string) (doc: string) =
    Regex.Matches(doc, "^" + Regex.Escape(heading) + "$", RegexOptions.Multiline).Count

[<Fact>]
let ``shipped docs match pinned versions and the v1 contract`` () =
    use sdkDoc = JsonDocument.Parse(text "global.json")
    let sdk = sdkDoc.RootElement.GetProperty("sdk")
    let sdkVersion = sdk.GetProperty("version").GetString()
    let rollForward = sdk.GetProperty("rollForward").GetString()
    Assert.False(isNull sdkVersion)
    Assert.False(isNull rollForward)

    let appProj = text "Carolina.fsproj"
    let testProj = text "Carolina.Tests/Carolina.Tests.fsproj"
    use toolsDoc = JsonDocument.Parse(text ".config/dotnet-tools.json")

    let fantomas =
        toolsDoc.RootElement.GetProperty("tools").GetProperty("fantomas").GetProperty("version").GetString()

    Assert.False(isNull fantomas)

    let docker = text "Dockerfile"
    let tfm = capture "<TargetFramework>([^<]+)</TargetFramework>" appProj
    let testTfm = capture "<TargetFramework>([^<]+)</TargetFramework>" testProj
    let npgsql = capture "Include=\"Npgsql\" Version=\"([^\"]+)\"" appProj
    let xunit = capture "Include=\"xunit\" Version=\"([^\"]+)\"" testProj

    let testSdk =
        capture "Include=\"Microsoft.NET.Test.Sdk\" Version=\"([^\"]+)\"" testProj

    let runner =
        capture "Include=\"xunit.runner.visualstudio\" Version=\"([^\"]+)\"" testProj

    let aspnet = capture "mcr\\.microsoft\\.com/dotnet/aspnet:([^\\s]+)" docker
    let sdkImage = capture "mcr\\.microsoft\\.com/dotnet/sdk:([^\\s]+)" docker
    let gitleaks = capture "gitleaks = \"([^\"]+)\"" (text "mise.toml")

    Assert.Equal(tfm, testTfm)
    Assert.Contains("PublishReadyToRun", appProj)
    Assert.Contains("Microsoft.NET.Sdk.Web", appProj)

    let agents = text "AGENTS.md"
    let readme = text "README.md"
    let memory = text "MEMORY.md"
    let decisions = text "DECISIONS.md"

    let pins =
        [ sdkVersion
          rollForward
          tfm
          npgsql
          xunit
          testSdk
          runner
          fantomas
          gitleaks
          "aspnet:" + aspnet
          "sdk:" + sdkImage
          "PublishReadyToRun"
          "Microsoft.NET.Sdk.Web"
          "ASP.NET" ]

    require "README.md" readme pins
    require "MEMORY.md" memory pins

    require
        "AGENTS.md"
        agents
        [ "v1_speakers"
          "v1_sponsors"
          "v1_years"
          "v1_talks"
          "v1_sponsorships"
          "v1_year_speakers"
          "v1_year_sponsors"
          "Ash"
          "base tables"
          "DATABASE_URL"
          "CAROLINA_URL"
          "POLYGLOT_REGISTER_TOKEN"
          "PUBLIC_BASE_URL"
          "PORT"
          "GET /health"
          "{ \"data\": [ ... ] }"
          "Register once on boot"
          "no heartbeat"
          "log and continue"
          "does not touch the database"
          "fake catalog"
          "openapi.yaml"
          "priv/api/AGENTS.md"
          "../elixir"
          "MEMORY.md"
          "DECISIONS.md"
          "Accepted decisions are binding"
          "When a durable choice changes, update `DECISIONS.md`"
          "src/"
          "db/*.sql"
          "images/"
          "docker-compose.yml"
          "tests/test_catalog.py"
          "Postgres 16" ]

    require
        "DECISIONS.md"
        decisions
        [ "Status: Accepted"
          "### Context"
          "### Decision"
          "### Consequences"
          sdkVersion
          rollForward
          tfm
          npgsql
          xunit
          fantomas
          "aspnet:" + aspnet
          "PublishReadyToRun"
          "read-only"
          "No writes"
          "Register once"
          "No heartbeat"
          "log and continue"
          "Lazy catalog"
          "does not touch the database"
          "workstation GC"
          "min pool 0"
          "IPv6"
          "suspend"
          "512 MB"
          "fake catalog"
          "TreatWarningsAsErrors"
          "Semgrep"
          "NuGet audit"
          "gitleaks"
          "Fantomas"
          "v1_speakers" ]

    Assert.True(headingCount "### Context" decisions >= 6)
    Assert.True(headingCount "### Decision" decisions >= 6)
    Assert.True(headingCount "### Consequences" decisions >= 6)

    let privateMarkers = [ "/home/barry"; "zebra-hydra"; "BEGIN " ]

    for docName, doc in
        [ "AGENTS.md", agents
          "README.md", readme
          "MEMORY.md", memory
          "DECISIONS.md", decisions ] do
        let hits = privateMarkers |> List.filter (fun marker -> doc.Contains marker)

        Assert.True(List.isEmpty hits, sprintf "%s contains %s" docName (String.concat ", " hits))
