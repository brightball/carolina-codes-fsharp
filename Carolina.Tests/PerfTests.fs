module PerfTests

open System
open System.IO
open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

let findFile name =
    let starts =
        [ Directory.GetCurrentDirectory()
          AppContext.BaseDirectory
          Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..") ]
    starts
    |> Seq.collect (fun root ->
        [ Path.Combine(root, name)
          Path.Combine(root, "..", name)
          Path.Combine(root, "fsharp", name) ])
    |> Seq.map Path.GetFullPath
    |> Seq.tryFind File.Exists
    |> Option.defaultWith (fun () -> failwithf "could not find %s" name)

[<Fact>]
let ``listen bind is IPv6 dual-stack not 0.0.0.0`` () =
    Assert.Equal("http://[::]:8080", Program.listenUrl "8080")
    let src = File.ReadAllText(findFile "Program.fs")
    Assert.Contains("listenUrl", src)
    Assert.DoesNotContain("http://0.0.0.0", src)
    let docker = File.ReadAllText(findFile "Dockerfile")
    Assert.Contains("ASPNETCORE_URLS=http://[::]:8080", docker)
    Assert.DoesNotContain("ASPNETCORE_URLS=http://0.0.0.0:8080", docker)

[<Fact>]
let ``register-once does not open Postgres or run catalog SQL`` () =
    let src = File.ReadAllText(findFile "Program.fs")
    let start = src.IndexOf("let register ")
    Assert.True(start >= 0)
    let ending = src.IndexOf("let allDigits", start)
    let fn = src.Substring(start, ending - start)
    Assert.DoesNotContain("withConn", fn)
    Assert.DoesNotContain("openCatalog", fn)
    Assert.DoesNotContain("execCmd", fn)
    Assert.DoesNotContain("OpenConnection", fn)

[<Fact>]
let ``health does not open Postgres or run SQL`` () =
    Program.resetCatalog ()
    let status, node = Program.handle "/health" ""
    Assert.Equal(200, status)
    Assert.Contains("ok", node.ToJsonString())
    Assert.Equal(0, Program.sqlCount)
    Assert.Equal(0, Program.connectCount)

[<Fact>]
let ``pooled sessions are reused across requests`` () =
    Program.resetCatalog ()
    Program.openHook <- Some(fun _ -> ())
    Program.openCatalog "unused"
    Assert.Equal(1, Program.connectCount)
    let a, _ = Program.handle "/health" ""
    let b, _ = Program.handle "/health" ""
    Assert.Equal(200, a)
    Assert.Equal(200, b)
    Assert.Equal(1, Program.connectCount)
    Assert.Equal(0, Program.sqlCount)

[<Fact>]
let ``year speaker listing SQL is bounded not 2N`` () =
    Program.resetCatalog ()
    let dsn =
        Program.toNpgsql (Program.env "DATABASE_URL" "postgres://postgres:postgres@127.0.0.1:5432/carolina_dev")
    Program.openCatalog dsn
    let connects = Program.connectCount
    Program.sqlCount <- 0
    let status, node = Program.handle "/v1/speakers" "2026"
    let body = node.ToJsonString()
    let sql = Program.sqlCount
    let speakers =
        let rec count (s: string) (acc: int) (i: int) =
            match s.IndexOf("\"talks\":", i) with
            | -1 -> acc
            | n -> count s (acc + 1) (n + 8)
        count body 0 0
    eprintfn "year list status=%d sql=%d speakers=%d connects=%d" status sql speakers Program.connectCount
    if status = 200 then
        Assert.True(speakers >= 3, sprintf "expected N>=3 speakers, got %d" speakers)
        Assert.True(sql > 0)
        Assert.True(sql < 2 * speakers, sprintf "sql %d grew like 2N for N=%d" sql speakers)
        Assert.True(sql <= 4, sprintf "sql %d should be speakers+talks+years" sql)
        Program.sqlCount <- 0
        let status2, _ = Program.handle "/v1/speakers" "2026"
        Assert.Equal(200, status2)
        Assert.Equal(connects, Program.connectCount)
    else
        Assert.True(sql < 6)
