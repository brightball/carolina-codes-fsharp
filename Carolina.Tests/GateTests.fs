module GateTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Xunit

let repoFile name =
    File.ReadAllText(TestPaths.findFile name)

let checkJobs = [ "test"; "sast"; "audit"; "gitleaks"; "style" ]

let cloneCmd =
    "git clone --depth 1 --no-checkout \"https://x-access-token:${token}@${host}/${GITHUB_REPOSITORY}\" ."

let makeTarget =
    function
    | "test" -> "make test"
    | "sast" -> "make sast"
    | "audit" -> "make audit"
    | "gitleaks" -> "make secrets"
    | "style" -> "make lint"
    | name -> failwithf "unknown check job %s" name

let workflowJobs (text: string) =
    let normalized = text.Replace("\r\n", "\n")

    let jobsBlock =
        match Regex.Match(normalized, @"^jobs:\n(.*)\z", RegexOptions.Singleline ||| RegexOptions.Multiline) with
        | m when m.Success -> m.Groups[1].Value
        | _ -> failwith "missing jobs: block"

    Regex.Matches(jobsBlock, @"^  ([A-Za-z0-9_-]+):\n([\s\S]*?)(?=^  [A-Za-z0-9_-]+:|\z)", RegexOptions.Multiline)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
    |> Map.ofSeq

let jobNeeds (body: string) =
    let scalar =
        Regex.Match(body, @"^[ ]{4}needs:\s*([A-Za-z0-9_-]+)\s*$", RegexOptions.Multiline)

    if scalar.Success then
        [ scalar.Groups[1].Value ]
    else
        Regex.Matches(body, @"^[ ]{4}needs:\s*\n((?:[ ]{6}-\s+[A-Za-z0-9_-]+\s*\n)+)", RegexOptions.Multiline)
        |> Seq.cast<Match>
        |> Seq.collect (fun m ->
            Regex.Matches(m.Groups[1].Value, @"-\s+([A-Za-z0-9_-]+)")
            |> Seq.cast<Match>
            |> Seq.map (fun n -> n.Groups[1].Value))
        |> List.ofSeq

[<Fact>]
let ``pre-commit config lists all five checks`` () =
    let text = repoFile ".pre-commit-config.yaml"
    Assert.Contains("id: local-tests", text)
    Assert.Contains("id: sast", text)
    Assert.Contains("id: audit", text)
    Assert.Contains("id: gitleaks", text)
    Assert.Contains("id: style", text)
    Assert.Contains("entry: make test", text)
    Assert.Contains("entry: make sast", text)
    Assert.Contains("entry: make audit", text)
    Assert.Contains("entry: make secrets", text)
    Assert.Contains("entry: make lint", text)
    Assert.Contains("SKIP=", text)

[<Fact>]
let ``gitea workflow has prep plus five check jobs not one combined gate`` () =
    let text = repoFile ".gitea/workflows/precommit.yml"
    Assert.Contains("\non:\n", text.Replace("\r\n", "\n"))
    Assert.Contains("push:", text)
    Assert.Contains("pull_request:", text)

    let jobs = workflowJobs text
    let jobNames = jobs |> Map.keys |> Set.ofSeq

    Assert.True(jobNames.Contains "prep", sprintf "jobs=%A" jobNames)

    for name in checkJobs do
        Assert.True(jobNames.Contains name, sprintf "missing %s jobs=%A" name jobNames)

    Assert.Equal(6, jobNames.Count)

    let combined =
        Regex.IsMatch(
            text.Replace("\r\n", "\n"),
            @"run:\s*\|\s*\n(?:.*\n)*?(?:make check|pre-commit run --all-files)",
            RegexOptions.Multiline
        )

    Assert.False(combined, "workflow must not use a single combined pre-commit/make check step as the gate")
    Assert.DoesNotContain("make check", text)
    Assert.DoesNotContain("pre-commit run --all-files", text)
    Assert.Contains("make test", text)
    Assert.Contains("make sast", text)
    Assert.Contains("make audit", text)
    Assert.Contains("make secrets", text)
    Assert.Contains("make lint", text)

[<Fact>]
let ``gitea check jobs need only prep and each runs one make target`` () =
    let jobs = workflowJobs (repoFile ".gitea/workflows/precommit.yml")
    let prep = Map.find "prep" jobs
    Assert.Equal<string list>([], jobNeeds prep)

    for name in checkJobs do
        let body = Map.find name jobs
        Assert.Equal<string list>([ "prep" ], jobNeeds body)

        for other in checkJobs do
            Assert.False(body.Contains($"needs: {other}"), sprintf "%s must not need %s" name other)

        let target = makeTarget name
        Assert.Contains(target, body)

        for other in checkJobs do
            if other <> name then
                Assert.DoesNotContain(makeTarget other, body)

        Assert.DoesNotContain("make check", body)
        Assert.DoesNotContain("pre-commit run --all-files", body)

[<Fact>]
let ``gitea prep clones and installs once; check jobs restore the workspace artifact`` () =
    let jobs = workflowJobs (repoFile ".gitea/workflows/precommit.yml")
    let prep = Map.find "prep" jobs

    Assert.Contains(cloneCmd, prep)
    Assert.Contains("git fetch --depth 1 origin \"${GITHUB_SHA}\"", prep)
    Assert.Contains("missing job token for git fetch", prep)
    Assert.Contains("make restore", prep)
    Assert.Contains("semgrep==1.97.0", prep)
    Assert.Contains("gitleaks_8.30.1_linux_x64.tar.gz", prep)
    Assert.Contains(".ci/semgrep", prep)
    Assert.Contains(".ci/bin", prep)
    Assert.Contains("NUGET_PACKAGES", prep)
    Assert.Contains("tar -czf /tmp/prep-workspace.tar.gz", prep)
    Assert.Contains("--exclude=./.git", prep)
    Assert.Contains("actions/upload-artifact@v3", prep)
    Assert.Contains("name: prep-workspace", prep)
    Assert.DoesNotContain("needs: prep", prep)
    Assert.DoesNotContain("git init", prep)

    for name in checkJobs do
        let body = Map.find name jobs
        Assert.Contains("actions/download-artifact@v3", body)
        Assert.Contains("name: prep-workspace", body)
        Assert.Contains("prep-workspace.tar.gz", body)
        Assert.DoesNotContain(cloneCmd, body)
        Assert.DoesNotContain("git clone", body)
        Assert.DoesNotContain("make restore", body)
        Assert.DoesNotContain("dotnet restore", body)
        Assert.DoesNotContain("dotnet tool restore", body)
        Assert.DoesNotContain("pip3 install", body)
        Assert.DoesNotContain("pip install", body)
        Assert.DoesNotContain("gitleaks_8.30.1_linux_x64.tar.gz", body)
        Assert.DoesNotContain("python3-pip", body)

        Assert.False(
            Regex.IsMatch(body, @"apt-get install[^\n]*\bgit\b"),
            sprintf "%s must not apt-get git for a clone" name
        )

[<Fact>]
let ``makefile reuses a vendored ci cache instead of a network restore`` () =
    let text = repoFile "Makefile"
    Assert.Contains(".ci/nuget", text)
    Assert.Contains("using vendored .ci/nuget", text)
    Assert.Contains(".ci/semgrep", text)
    Assert.Contains(".ci/bin", text)
    Assert.Contains("NUGET_PACKAGES", text)
    Assert.Contains("PYTHONPATH", text)
    Assert.Contains("test: restore", text)
    Assert.Contains("audit: restore", text)
    Assert.Contains("lint: restore", text)
    Assert.Contains("$(DOTNET) test Carolina.sln --nologo --no-restore", text)
    Assert.Contains("--exclude .ci", text)

    let fantomasIgnore = repoFile ".fantomasignore"
    Assert.Contains(".ci/", fantomasIgnore)

    let gitleaks = repoFile ".gitleaks.toml"
    Assert.Contains(@"\.ci/", gitleaks)
    Assert.Contains("useDefault = true", gitleaks)
    Assert.Contains("--no-git", text)

let runMakeDryRestore (withNugetCache: bool) =
    let makefile = TestPaths.findFile "Makefile"

    let tmp =
        Path.Combine(Path.GetTempPath(), "carolina-ci-restore-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tmp |> ignore

    if withNugetCache then
        let nuget = Path.Combine(tmp, ".ci", "nuget")
        Directory.CreateDirectory nuget |> ignore
        File.WriteAllText(Path.Combine(nuget, "marker"), "x")

    try
        let psi =
            ProcessStartInfo(
                FileName = "make",
                Arguments = sprintf "-n -C \"%s\" -f \"%s\" restore" tmp makefile,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        use proc =
            match Process.Start psi with
            | null -> failwith "failed to start make"
            | started -> started

        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()
        Assert.True(proc.ExitCode = 0, sprintf "make -n restore failed: %s%s" stdout stderr)
        stdout
    finally
        Directory.Delete(tmp, true)

[<Fact>]
let ``makefile restore is skipped when ci nuget cache exists`` () =
    let stdout = runMakeDryRestore true
    Assert.Contains("using vendored .ci/nuget", stdout)
    Assert.DoesNotContain("restore Carolina.sln", stdout)
    Assert.DoesNotContain("tool restore", stdout)

[<Fact>]
let ``makefile restore fetches tools when no ci nuget cache exists`` () =
    let stdout = runMakeDryRestore false
    Assert.Contains("restore Carolina.sln", stdout)
    Assert.Contains("tool restore", stdout)
    Assert.DoesNotContain("using vendored .ci/nuget", stdout)

let repoRoot () =
    match Path.GetDirectoryName(TestPaths.findFile "Makefile") with
    | null
    | "" -> failwith "could not resolve repo root"
    | dir -> dir

let findGitleaks () =
    let vendored = Path.Combine(repoRoot (), ".ci", "bin", "gitleaks")

    if File.Exists vendored then
        vendored
    else
        let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

        let mise =
            Path.Combine(home, ".local", "share", "mise", "installs", "gitleaks", "latest", "gitleaks")

        if File.Exists mise then
            mise
        else
            match Environment.GetEnvironmentVariable "PATH" with
            | null
            | "" -> failwith "gitleaks not found (empty PATH)"
            | path ->
                path.Split Path.PathSeparator
                |> Seq.map (fun dir -> Path.Combine(dir, "gitleaks"))
                |> Seq.tryFind File.Exists
                |> Option.defaultWith (fun () -> failwith "gitleaks not found on PATH or in .ci/bin")

let runMakeSecrets (workDir: string) =
    let psi =
        ProcessStartInfo(
            FileName = "make",
            Arguments = "secrets",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    use proc =
        match Process.Start psi with
        | null -> failwith "failed to start make secrets"
        | started -> started

    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    stdout + stderr, proc.ExitCode

let bytesScanned (output: string) =
    let m = Regex.Match(output, @"scanned ~(\d+) bytes")
    if m.Success then int64 m.Groups[1].Value else 0L

[<Fact>]
let ``make secrets scans a gitless tree instead of a silent empty git scan`` () =
    let root = repoRoot ()

    let tmp =
        Path.Combine(Path.GetTempPath(), "carolina-gitless-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tmp |> ignore
    Directory.CreateDirectory(Path.Combine(tmp, ".ci", "bin")) |> ignore

    try
        File.Copy(Path.Combine(root, "Makefile"), Path.Combine(tmp, "Makefile"))
        File.Copy(Path.Combine(root, "Program.fs"), Path.Combine(tmp, "Program.fs"))
        File.Copy(Path.Combine(root, ".gitleaks.toml"), Path.Combine(tmp, ".gitleaks.toml"))
        File.Copy(findGitleaks (), Path.Combine(tmp, ".ci", "bin", "gitleaks"))

        let bin = Path.Combine(tmp, ".ci", "bin", "gitleaks")
        let mode = File.GetUnixFileMode bin

        File.SetUnixFileMode(
            bin,
            mode
            ||| UnixFileMode.UserExecute
            ||| UnixFileMode.GroupExecute
            ||| UnixFileMode.OtherExecute
        )

        Assert.False(Directory.Exists(Path.Combine(tmp, ".git")))

        let output, exitCode = runMakeSecrets tmp
        Assert.True((exitCode = 0), sprintf "make secrets failed: %s" output)
        Assert.DoesNotContain("not a git repository", output)
        Assert.True(bytesScanned output > 0L, sprintf "expected non-zero bytes scanned, got: %s" output)
    finally
        Directory.Delete(tmp, true)

[<Fact>]
let ``make secrets still uses default gitleaks rules on a gitless tree`` () =
    let root = repoRoot ()

    let tmp =
        Path.Combine(Path.GetTempPath(), "carolina-leak-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tmp |> ignore
    Directory.CreateDirectory(Path.Combine(tmp, ".ci", "bin")) |> ignore

    try
        File.Copy(Path.Combine(root, "Makefile"), Path.Combine(tmp, "Makefile"))
        File.Copy(Path.Combine(root, ".gitleaks.toml"), Path.Combine(tmp, ".gitleaks.toml"))
        File.Copy(findGitleaks (), Path.Combine(tmp, ".ci", "bin", "gitleaks"))

        // Built at runtime so this source file is not itself a committed secret.
        let marker =
            String.concat "" [ "xoxb-"; "123456789012-"; "1234567890123-"; "abcdefghijklmnopqrstuvwx" ]

        File.WriteAllText(Path.Combine(tmp, "leak.txt"), sprintf "token = \"%s\"\n" marker)

        let bin = Path.Combine(tmp, ".ci", "bin", "gitleaks")
        let mode = File.GetUnixFileMode bin

        File.SetUnixFileMode(
            bin,
            mode
            ||| UnixFileMode.UserExecute
            ||| UnixFileMode.GroupExecute
            ||| UnixFileMode.OtherExecute
        )

        let output, exitCode = runMakeSecrets tmp
        Assert.True((exitCode <> 0), sprintf "expected leaks found, got exit %d: %s" exitCode output)
        Assert.Contains("leaks found", output)
        Assert.True(bytesScanned output > 0L, sprintf "expected non-zero bytes scanned, got: %s" output)
        Assert.DoesNotContain("not a git repository", output)
    finally
        Directory.Delete(tmp, true)

let msbuildProperty (project: string) (configuration: string) (name: string) =
    let psi =
        ProcessStartInfo(
            FileName = "dotnet",
            Arguments = sprintf "msbuild \"%s\" -nologo -getProperty:%s -p:Configuration=%s" project name configuration,
            WorkingDirectory = repoRoot (),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    use proc =
        match Process.Start psi with
        | null -> failwith "failed to start dotnet msbuild"
        | started -> started

    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    Assert.True(proc.ExitCode = 0, sprintf "msbuild %s failed: %s%s" name stdout stderr)
    stdout.Trim()

[<Fact>]
let ``fsharp warnings fail the existing test build`` () =
    let props = repoFile "Directory.Build.props"
    Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", props)

    let makefile = repoFile "Makefile"
    Assert.Contains("$(DOTNET) test Carolina.sln --nologo --no-restore", makefile)
    Assert.DoesNotContain("TreatWarningsAsErrors=false", makefile)

    for project in [ "Carolina.fsproj"; "Carolina.Tests/Carolina.Tests.fsproj" ] do
        let value =
            (msbuildProperty project "Debug" "TreatWarningsAsErrors").ToLowerInvariant()

        Assert.Equal("true", value)

[<Fact>]
let ``release publish precompiles and semgrep still scans F#`` () =
    let release =
        (msbuildProperty "Carolina.fsproj" "Release" "PublishReadyToRun").ToLowerInvariant()

    Assert.Equal("true", release)

    let gc =
        (msbuildProperty "Carolina.fsproj" "Release" "ServerGarbageCollection").ToLowerInvariant()

    Assert.Equal("false", gc)

    let semgrep = repoFile "semgrep.yml"
    Assert.Contains("*.fs", semgrep)
    Assert.Contains("languages: [generic]", semgrep)
