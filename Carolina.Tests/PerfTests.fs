module PerfTests

open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

[<Fact>]
let ``listen bind is IPv6 dual-stack not 0.0.0.0`` () =
    Assert.Equal("http://[::]:8080", Program.listenUrl "8080")
    let src = File.ReadAllText(TestPaths.findFile "Program.fs")
    Assert.Contains("listenUrl", src)
    Assert.Contains("PreferHostingUrls", src)
    Assert.DoesNotContain("http://0.0.0.0", src)
    let docker = File.ReadAllText(TestPaths.findFile "Dockerfile")
    Assert.Contains("ASPNETCORE_URLS=http://[::]:8080", docker)
    Assert.Contains("-r linux-x64", docker)
    Assert.Contains("PublishReadyToRun=true", docker)
    Assert.Contains("DOTNET_gcServer=0", docker)
    Assert.Contains("DOTNET_ReadyToRun=1", docker)
    Assert.Contains("--self-contained false", docker)
    Assert.DoesNotContain("ASPNETCORE_URLS=http://0.0.0.0:8080", docker)
    Assert.DoesNotContain("0.0.0.0", docker)

[<Fact>]
let ``boot does not open the catalog or register before listen`` () =
    let src = File.ReadAllText(TestPaths.findFile "Program.fs")
    let start = src.IndexOf("[<EntryPoint>]")
    Assert.True(start >= 0)
    let fn = src.Substring(start)
    Assert.Contains("ApplicationStarted", fn)
    Assert.Contains("listenUrl", fn)
    Assert.Contains("PreferHostingUrls", fn)
    Assert.Contains("register", fn)
    // Run(handler) only installs middleware. The host starts on the parameterless call.
    Assert.Contains("app.Run()", fn)
    Assert.DoesNotContain("openCatalog", fn)
    Assert.DoesNotContain("ensureCatalog", fn)
    Assert.DoesNotContain("OpenConnection", fn)
    Assert.DoesNotContain("NpgsqlDataSource", fn)

[<Fact>]
let ``register-once does not open Postgres or run catalog SQL`` () =
    let src = File.ReadAllText(TestPaths.findFile "Program.fs")
    let start = src.IndexOf("let register ")
    Assert.True(start >= 0)
    let ending = src.IndexOf("let allDigits", start)
    let fn = src.Substring(start, ending - start)
    Assert.DoesNotContain("withConn", fn)
    Assert.DoesNotContain("openCatalog", fn)
    Assert.DoesNotContain("ensureCatalog", fn)
    Assert.DoesNotContain("execCmd", fn)
    Assert.DoesNotContain("OpenConnection", fn)
    Assert.DoesNotContain("query ", fn)

[<Fact>]
let ``health and identity do not open Postgres or run SQL`` () =
    Program.resetCatalog ()
    let health = Program.dispatch "GET" "/health" ""
    let root = Program.dispatch "GET" "/" ""
    Assert.Equal(200, health.Status)
    Assert.Equal(200, root.Status)
    Assert.Contains("\"ok\":true", health.Body.Value)
    Assert.Contains("\"language\":\"F#\"", root.Body.Value)
    Assert.Equal(0, Program.sqlCount)
    Assert.Equal(0, Program.connectCount)

[<Fact>]
let ``pooled sessions are reused across requests`` () =
    Program.resetCatalog ()
    Program.openHook <- Some(fun _ -> ())
    Program.openCatalog "unused"
    Assert.Equal(1, Program.connectCount)
    let a = Program.dispatch "GET" "/health" ""
    let b = Program.dispatch "GET" "/" ""
    Assert.Equal(200, a.Status)
    Assert.Equal(200, b.Status)
    Assert.Equal(1, Program.connectCount)
    Assert.Equal(0, Program.sqlCount)

let yearSpeakerHook n (sql: string) _ps =
    if sql.Contains("FROM v1_speakers WHERE slug IN") then
        [ for i in 1..n ->
              Program.jObj
                  [ "slug", Program.jStr (sprintf "s%d" i)
                    "name", Program.jStr (sprintf "Speaker %d" i) ] ]
    elif sql.Contains("FROM v1_talks WHERE year =") then
        [ for i in 1..n ->
              Program.jObj
                  [ "slug", Program.jStr (sprintf "talk-%d" i)
                    "speaker_slug", Program.jStr (sprintf "s%d" i)
                    "year", Program.jInt 2026
                    "languages", Program.jArr [ Program.jStr "fsharp" ]
                    "topics", Program.jArr [ Program.jStr "runtime" ] ] ]
    elif sql.Contains("DISTINCT speaker_slug, year") then
        [ for i in 1..n -> Program.jObj [ "speaker_slug", Program.jStr (sprintf "s%d" i); "year", Program.jInt 2026 ] ]
    else
        failwithf "year list issued unexpected sql: %s" sql

[<Fact>]
let ``year speaker listing SQL is bounded not 2N`` () =
    let n = 5
    Program.resetCatalog ()
    let statements = ResizeArray<string>()

    Program.queryHook <-
        Some(fun sql ps ->
            statements.Add(sql)
            yearSpeakerHook n sql ps)

    let reply = Program.dispatch "GET" "/v1/speakers" "2026"
    Assert.Equal(200, reply.Status)

    match JsonNode.Parse(reply.Body.Value) with
    | null -> failwith "empty body"
    | node ->
        match node["data"] with
        | null -> failwith "missing data"
        | data ->
            let count = data.AsArray().Count
            Assert.True(count >= 3, sprintf "expected N>=3 speakers, got %d" count)
            Assert.True(Program.sqlCount > 0)
            Assert.True(Program.sqlCount <= 4, sprintf "sql %d should be at most 4" Program.sqlCount)
            Assert.True(Program.sqlCount < 2 * count, sprintf "sql %d grew like 2N for N=%d" Program.sqlCount count)

            let perSpeaker =
                statements
                |> Seq.filter (fun sql -> sql.Contains("WHERE speaker_slug = $1") || sql.Contains("WHERE slug = $1"))
                |> Seq.toList

            Assert.Empty(perSpeaker)
            let connects = Program.connectCount
            Assert.Equal(1, connects)
            Program.sqlCount <- 0
            statements.Clear()
            let again = Program.dispatch "GET" "/v1/speakers" "2026"
            Assert.Equal(200, again.Status)
            Assert.Equal(connects, Program.connectCount)

            match JsonNode.Parse(again.Body.Value) with
            | null -> failwith "empty second body"
            | second ->
                match second["data"] with
                | null -> failwith "missing second data"
                | rows -> Assert.Equal(count, rows.AsArray().Count)

            Assert.True(Program.sqlCount <= 4)
            Assert.True(Program.sqlCount < 2 * count)

[<Fact>]
let ``dead pooled connection is retried once and health stays ok`` () =
    Program.resetCatalog ()
    Program.deadConnectionsRemaining <- 1

    Program.queryHook <-
        Some(fun sql _ ->
            Assert.Contains("v1_years", sql)

            [ Program.jObj
                  [ "year", Program.jInt 2026
                    "slug", Program.jStr "2026"
                    "name", Program.jStr "Carolina Code Conference 2026"
                    "status", Program.jStr "past" ] ])

    let reply = Program.dispatch "GET" "/v1/years" ""
    Assert.Equal(200, reply.Status)
    Assert.Contains("2026", reply.Body.Value)
    Assert.Equal(2, Program.connectCount)
    Assert.Equal(1, Program.sqlCount)
    Assert.Equal(0, Program.deadConnectionsRemaining)

    let health = Program.dispatch "GET" "/health" ""
    Assert.Equal(200, health.Status)
    Assert.Contains("\"ok\":true", health.Body.Value)
    Assert.Equal(1, Program.sqlCount)
    Assert.Equal(2, Program.connectCount)

[<Fact>]
let ``a second dead connection still leaves the process able to serve health`` () =
    Program.resetCatalog ()
    Program.deadConnectionsRemaining <- 2

    Program.queryHook <- Some(fun _ _ -> failwith "the failed attempts must not run the query")

    let reply = Program.dispatch "GET" "/v1/years" ""
    Assert.Equal(500, reply.Status)
    Assert.Equal(2, Program.connectCount)
    Assert.Equal(0, Program.sqlCount)

    let health = Program.dispatch "GET" "/health" ""
    Assert.Equal(200, health.Status)
    Assert.Contains("\"ok\":true", health.Body.Value)
    Assert.Equal(0, Program.sqlCount)

[<Fact>]
let ``fly machines suspend without swap, schedule, or more than 2 GB`` () =
    let fly = File.ReadAllText(TestPaths.findFile "fly.toml")
    Assert.Contains("auto_stop_machines = \"suspend\"", fly)
    Assert.DoesNotContain("auto_stop_machines = \"stop\"", fly)
    Assert.DoesNotContain("auto_stop_machines = \"off\"", fly)
    Assert.Contains("auto_start_machines = true", fly)
    Assert.Contains("min_machines_running = 0", fly)
    Assert.Contains("method = \"GET\"", fly)
    Assert.Contains("path = \"/health\"", fly)
    Assert.DoesNotContain("swap_size_mb", fly)
    Assert.DoesNotContain("[schedule]", fly)
    Assert.False(Regex.IsMatch(fly, @"(?m)^\s*schedule\s*="), "fly.toml must not configure a machine schedule")

    let mem =
        Regex.Match(fly, "memory\\s*=\\s*\"(\\d+)(mb|gb)\"", RegexOptions.IgnoreCase)

    Assert.True(mem.Success, "fly.toml must set [[vm]] memory")

    let mb =
        if mem.Groups[2].Value.Equals("gb", System.StringComparison.OrdinalIgnoreCase) then
            int mem.Groups[1].Value * 1024
        else
            int mem.Groups[1].Value

    Assert.True(mb > 0 && mb <= 2048, sprintf "suspend requires <= 2048 MB, got %d" mb)
