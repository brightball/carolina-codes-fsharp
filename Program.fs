module Program

open System
open System.Data.Common
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Npgsql
open NpgsqlTypes

let language = "F#"
let apiVersion = "0.2.0"
let framework = "ASP.NET"
let createdYear = 2026
let schemaVersion = 1
let languageVersion = Environment.Version.ToString()

let endpoints: JsonNode =
    JsonNode.Parse(
        """[
          {"method":"GET","path":"/","query":[]},
          {"method":"GET","path":"/health","query":[]},
          {"method":"GET","path":"/v1/years","query":[]},
          {"method":"GET","path":"/v1/speakers","query":["year"]},
          {"method":"GET","path":"/v1/speakers/:slug","query":[]},
          {"method":"GET","path":"/v1/speakers/:year/:slug","query":[]},
          {"method":"GET","path":"/v1/sponsors","query":["year"]},
          {"method":"GET","path":"/v1/sponsors/:slug","query":[]},
          {"method":"GET","path":"/v1/sponsors/:year/:slug","query":[]}
        ]"""
    )

let env name fallback =
    match Environment.GetEnvironmentVariable(name) with
    | null
    | "" -> fallback
    | v -> v

let mutable sqlCount = 0
let mutable connectCount = 0

// Test seam: injected dead pooled connections still left to fail. Stays 0 in production.
let mutable deadConnectionsRemaining = 0

let mutable dataSource: NpgsqlDataSource = Unchecked.defaultof<_>
let mutable openHook: (string -> unit) option = None
let mutable queryHook: (string -> obj list -> JsonNode list) option = None
let mutable catalogOpened = false
let mutable catalogDsn = ""
let catalogLock = obj ()

exception CatalogConnectionDead of string

let resetCatalog () =
    sqlCount <- 0
    connectCount <- 0
    deadConnectionsRemaining <- 0
    openHook <- None
    queryHook <- None
    catalogDsn <- ""

    lock catalogLock (fun () ->
        catalogOpened <- false

        if not (isNull dataSource) then
            dataSource.Dispose()
            dataSource <- Unchecked.defaultof<_>)

let openCatalogUnlocked (dsn: string) =
    connectCount <- connectCount + 1
    catalogOpened <- true
    catalogDsn <- dsn

    match openHook with
    | Some hook -> hook dsn
    | None when Option.isSome queryHook -> ()
    | None ->
        if not (isNull dataSource) then
            dataSource.Dispose()

        dataSource <- NpgsqlDataSource.Create(dsn)

let openCatalog dsn =
    lock catalogLock (fun () -> openCatalogUnlocked dsn)

let dropPool () =
    lock catalogLock (fun () ->
        try
            NpgsqlConnection.ClearAllPools()
        with _ ->
            ()

        if not (isNull dataSource) then
            try
                dataSource.Dispose()
            with _ ->
                ()

            dataSource <- Unchecked.defaultof<_>

        catalogOpened <- false)

let listenUrl (port: string) = $"http://[::]:{port}"

let hasConnKey (dsn: string) (key: string) =
    dsn.Split(';')
    |> Array.exists (fun part ->
        let name = part.Split('=').[0].Trim()
        String.Equals(name, key, StringComparison.OrdinalIgnoreCase))

let withConnDefault (dsn: string) key value =
    if hasConnKey dsn key then dsn else dsn.TrimEnd(';') + ";" + key + "=" + value

let toNpgsql (dsn: string) =
    let raw =
        if dsn.StartsWith("postgres://") || dsn.StartsWith("postgresql://") then
            let normalized =
                if dsn.StartsWith("postgres://") then
                    "http://" + dsn.Substring("postgres://".Length)
                else
                    "http://" + dsn.Substring("postgresql://".Length)

            let uri = Uri(normalized)
            let userInfo = uri.UserInfo.Split(':')

            let user =
                if userInfo.Length > 0 then Uri.UnescapeDataString(userInfo[0]) else "postgres"

            let pass =
                if userInfo.Length > 1 then
                    Uri.UnescapeDataString(String.Join(":", userInfo |> Array.skip 1))
                else
                    ""

            let db = uri.AbsolutePath.Trim('/')
            let port = if uri.IsDefaultPort || uri.Port < 0 then 5432 else uri.Port
            $"Host={uri.Host};Port={port};Username={user};Password={pass};Database={db};SSL Mode=Disable"
        else
            dsn

    raw
    |> fun value -> withConnDefault value "Timeout" "5"
    |> fun value -> withConnDefault value "Minimum Pool Size" "0"
    |> fun value -> withConnDefault value "Maximum Pool Size" "10"

let defaultDsn () =
    toNpgsql (env "DATABASE_URL" "postgres://postgres:postgres@127.0.0.1:5432/carolina_dev")

let configureCatalog dsn = catalogDsn <- dsn

let ensureCatalog () =
    if not catalogOpened then
        lock catalogLock (fun () ->
            if not catalogOpened then
                let dsn = if String.IsNullOrEmpty catalogDsn then defaultDsn () else catalogDsn

                openCatalogUnlocked dsn)

let jObj (fields: (string * JsonNode) list) =
    let o = JsonObject()

    for k, v in fields do
        o[k] <- v

    o :> JsonNode

let jArr (xs: JsonNode seq) =
    let a = JsonArray()

    for x in xs do
        a.Add(x)

    a :> JsonNode

let jNull: JsonNode = JsonValue.Create(null: string) :> JsonNode

let jStr (s: string) =
    if isNull s then jNull else JsonValue.Create(s) :> JsonNode

let jInt (n: int) = JsonValue.Create(n) :> JsonNode
let jBool (b: bool) = JsonValue.Create(b) :> JsonNode

let strOpt (r: DbDataReader) name =
    let i = r.GetOrdinal(name)
    if r.IsDBNull(i) then null else r.GetString(i)

let intCol (r: DbDataReader) name = r.GetInt32(r.GetOrdinal(name))

let boolCol (r: DbDataReader) name =
    let i = r.GetOrdinal(name)
    if r.IsDBNull(i) then false else r.GetBoolean(i)

let strArr (r: DbDataReader) name =
    let i = r.GetOrdinal(name)

    if r.IsDBNull(i) then
        [||]
    else
        match r.GetValue(i) with
        | :? (string[]) as xs -> xs
        | :? Array as arr ->
            [| for x in arr do
                   if not (isNull x) && x <> DBNull.Value then
                       string x |]
        | _ -> [||]

let speakerCols =
    "slug, first_name, last_name, name, tagline, bio, company, location, photo_path, twitter_url, linkedin_url, website_url, github_url, featured"

let yearSponsorCols =
    "slug, name, website, logo_path, description, blurb, tier, featured, year, twitter_url, linkedin_url, youtube_url, instagram_url, facebook_url"

let sponsorCols =
    "slug, name, website, logo_path, description, twitter_url, linkedin_url, youtube_url, instagram_url, facebook_url"

let talkCols =
    "slug, title, description, format, youtube_id, year, speaker_slug, languages, topics"

let speakerObj (r: DbDataReader) =
    jObj
        [ "slug", jStr (strOpt r "slug")
          "first_name", jStr (strOpt r "first_name")
          "last_name", jStr (strOpt r "last_name")
          "name", jStr (strOpt r "name")
          "tagline", jStr (strOpt r "tagline")
          "bio", jStr (strOpt r "bio")
          "company", jStr (strOpt r "company")
          "location", jStr (strOpt r "location")
          "photo_path", jStr (strOpt r "photo_path")
          "twitter_url", jStr (strOpt r "twitter_url")
          "linkedin_url", jStr (strOpt r "linkedin_url")
          "website_url", jStr (strOpt r "website_url")
          "github_url", jStr (strOpt r "github_url")
          "featured", jBool (boolCol r "featured") ]

let yearSponsorObj (r: DbDataReader) =
    jObj
        [ "slug", jStr (strOpt r "slug")
          "name", jStr (strOpt r "name")
          "website", jStr (strOpt r "website")
          "logo_path", jStr (strOpt r "logo_path")
          "description", jStr (strOpt r "description")
          "blurb", jStr (strOpt r "blurb")
          "tier", jStr (strOpt r "tier")
          "featured", jBool (boolCol r "featured")
          "year", jInt (intCol r "year")
          "twitter_url", jStr (strOpt r "twitter_url")
          "linkedin_url", jStr (strOpt r "linkedin_url")
          "youtube_url", jStr (strOpt r "youtube_url")
          "instagram_url", jStr (strOpt r "instagram_url")
          "facebook_url", jStr (strOpt r "facebook_url") ]

let sponsorObj (r: DbDataReader) =
    jObj
        [ "slug", jStr (strOpt r "slug")
          "name", jStr (strOpt r "name")
          "website", jStr (strOpt r "website")
          "logo_path", jStr (strOpt r "logo_path")
          "description", jStr (strOpt r "description")
          "twitter_url", jStr (strOpt r "twitter_url")
          "linkedin_url", jStr (strOpt r "linkedin_url")
          "youtube_url", jStr (strOpt r "youtube_url")
          "instagram_url", jStr (strOpt r "instagram_url")
          "facebook_url", jStr (strOpt r "facebook_url") ]

let talkObj (r: DbDataReader) =
    let langs = strArr r "languages"
    let topics = strArr r "topics"

    jObj
        [ "slug", jStr (strOpt r "slug")
          "title", jStr (strOpt r "title")
          "description", jStr (strOpt r "description")
          "format", jStr (strOpt r "format")
          "youtube_id", jStr (strOpt r "youtube_id")
          "year", jInt (intCol r "year")
          "speaker_slug", jStr (strOpt r "speaker_slug")
          "languages", jArr (langs |> Seq.map jStr)
          "topics", jArr (topics |> Seq.map jStr) ]

let uniq (xs: string seq) =
    let seen = Collections.Generic.HashSet<string>()

    [ for x in xs do
          if not (String.IsNullOrEmpty x) && seen.Add(x) then
              yield x ]

let jsonString (node: JsonNode) (name: string) =
    match node[name] with
    | null -> null
    | value when value.GetValueKind() = JsonValueKind.Null -> null
    | value -> value.GetValue<string>()

let jsonStrings (node: JsonNode) (name: string) =
    match node[name] with
    | null -> []
    | value when value.GetValueKind() = JsonValueKind.Null -> []
    | value ->
        value.AsArray()
        |> Seq.choose (fun item ->
            match item with
            | null -> None
            | node -> Some(node.GetValue<string>()))
        |> List.ofSeq

let groupTalks (rows: JsonNode list) =
    let map =
        Collections.Generic.Dictionary<string, ResizeArray<JsonNode> * ResizeArray<string> * ResizeArray<string>>()

    for row in rows do
        match jsonString row "speaker_slug" with
        | null -> ()
        | slug ->
            let talks, langs, topics =
                match map.TryGetValue(slug) with
                | true, found -> found
                | false, _ ->
                    let created = ResizeArray<JsonNode>(), ResizeArray<string>(), ResizeArray<string>()
                    map[slug] <- created
                    created

            talks.Add(row)
            langs.AddRange(jsonStrings row "languages")
            topics.AddRange(jsonStrings row "topics")

    [ for kv in map do
          let talks, langs, topics = kv.Value
          yield kv.Key, (talks |> Seq.toList, uniq langs, uniq topics) ]
    |> Map.ofList

let summarizeTalks (rows: JsonNode list) =
    let langs = rows |> List.collect (fun row -> jsonStrings row "languages")
    let topics = rows |> List.collect (fun row -> jsonStrings row "topics")
    rows, uniq langs, uniq topics

let groupYears (rows: JsonNode list) =
    let map = Collections.Generic.Dictionary<string, ResizeArray<int>>()

    for row in rows do
        match jsonString row "speaker_slug" with
        | null -> ()
        | slug ->
            if not (map.ContainsKey slug) then
                map[slug] <- ResizeArray<int>()

            match row["year"] with
            | null -> ()
            | yearNode -> map[slug].Add(yearNode.GetValue<int>())

    [ for kv in map -> kv.Key, kv.Value |> Seq.toList ] |> Map.ofList

let yearInts (rows: JsonNode list) =
    rows
    |> List.choose (fun node ->
        match node with
        | null -> None
        | value -> Some(value.GetValue<int>()))

let isRetryable (ex: exn) =
    match ex with
    | :? CatalogConnectionDead -> true
    | :? NpgsqlException -> true
    | :? TimeoutException -> true
    | :? IOException -> true
    | :? InvalidOperationException -> true
    | _ -> false

let addParams (cmd: NpgsqlCommand) (ps: obj list) =
    for p in ps do
        match p with
        | :? NpgsqlParameter as np -> cmd.Parameters.Add(np) |> ignore
        | value -> cmd.Parameters.AddWithValue(value) |> ignore

let textArray (slugs: string list) =
    let p = NpgsqlParameter()
    p.NpgsqlDbType <- NpgsqlDbType.Array ||| NpgsqlDbType.Text
    p.Value <- (slugs |> Array.ofList :> obj)
    p :> obj

let query (sql: string) (ps: obj list) (map: DbDataReader -> JsonNode) : JsonNode list =
    let execute () =
        if deadConnectionsRemaining > 0 then
            deadConnectionsRemaining <- deadConnectionsRemaining - 1
            raise (CatalogConnectionDead "dead pooled connection")

        match queryHook with
        | Some hook ->
            sqlCount <- sqlCount + 1
            hook sql ps
        | None ->
            if isNull dataSource then
                failwith "database not opened"

            use conn = dataSource.OpenConnection()
            use cmd = new NpgsqlCommand(sql, conn)
            addParams cmd ps
            sqlCount <- sqlCount + 1
            use reader = cmd.ExecuteReader()

            [ while reader.Read() do
                  yield map reader ]

    let once () =
        ensureCatalog ()
        execute ()

    try
        once ()
    with ex when isRetryable ex ->
        dropPool ()
        once ()

let queryOne (sql: string) (ps: obj list) (map: DbDataReader -> JsonNode) =
    match query sql ps map with
    | [] -> None
    | row :: _ -> Some row

let loadTalks (slug: string) (year: int option) =
    let sql, ps =
        match year with
        | Some y ->
            $"SELECT {talkCols} FROM v1_talks WHERE speaker_slug = $1 AND year = $2 ORDER BY year DESC",
            [ box slug; box y ]
        | None -> $"SELECT {talkCols} FROM v1_talks WHERE speaker_slug = $1 ORDER BY year DESC", [ box slug ]

    query sql ps talkObj |> summarizeTalks

let distinctYears sql slug =
    query sql [ box slug ] (fun r -> jInt (r.GetInt32(0))) |> yearInts

let talkYears slug =
    distinctYears "SELECT DISTINCT year FROM v1_talks WHERE speaker_slug = $1 ORDER BY year DESC" slug

let sponsorYears slug =
    distinctYears "SELECT DISTINCT year FROM v1_sponsorships WHERE sponsor_slug = $1 ORDER BY year DESC" slug

let exceptYear years year =
    years |> List.filter (fun y -> y <> year)

let loadSponsorships slug =
    query
        "SELECT sponsor_slug, year, tier, blurb, featured FROM v1_sponsorships WHERE sponsor_slug = $1 ORDER BY year DESC"
        [ box slug ]
        (fun r ->
            jObj
                [ "sponsor_slug", jStr (strOpt r "sponsor_slug")
                  "year", jInt (intCol r "year")
                  "tier", jStr (strOpt r "tier")
                  "blurb", jStr (strOpt r "blurb")
                  "featured", jBool (boolCol r "featured") ])

let merge (baseObj: JsonNode) (extra: (string * JsonNode) list) =
    let o = baseObj.AsObject()

    for k, v in extra do
        o[k] <- v

    o :> JsonNode

let loadTalksForYear (year: int) =
    query $"SELECT {talkCols} FROM v1_talks WHERE year = $1 ORDER BY speaker_slug, year DESC" [ box year ] talkObj
    |> groupTalks

let loadYearsForSlugs (slugs: string list) =
    if List.isEmpty slugs then
        Map.empty
    else
        query
            "SELECT DISTINCT speaker_slug, year FROM v1_talks WHERE speaker_slug = ANY($1::text[]) ORDER BY speaker_slug, year DESC"
            [ textArray slugs ]
            (fun r ->
                jObj
                    [ "speaker_slug", jStr (strOpt r "speaker_slug")
                      "year", jInt (intCol r "year") ])
        |> groupYears

let slugOf (node: JsonNode) =
    match node["slug"] with
    | null -> null
    | value when value.GetValueKind() = JsonValueKind.Null -> null
    | value -> value.GetValue<string>()

let listSpeakersYear (year: int) =
    let speakers =
        query
            $"SELECT {speakerCols} FROM v1_speakers WHERE slug IN (SELECT speaker_slug FROM v1_talks WHERE year = $1) ORDER BY last_name, first_name"
            [ box year ]
            speakerObj

    let slugs =
        speakers
        |> List.choose (fun sp ->
            match slugOf sp with
            | null -> None
            | slug -> Some slug)

    let talksBy = loadTalksForYear year
    let yearsBy = loadYearsForSlugs slugs

    speakers
    |> List.choose (fun sp ->
        match slugOf sp with
        | null -> None
        | slug ->
            let talks, langs, topics =
                match Map.tryFind slug talksBy with
                | Some found -> found
                | None -> [], [], []

            let years =
                match Map.tryFind slug yearsBy with
                | Some ys -> ys
                | None -> []

            Some(
                merge
                    sp
                    [ "year", jInt year
                      "talks", jArr talks
                      "languages", jArr (langs |> Seq.map jStr)
                      "topics", jArr (topics |> Seq.map jStr)
                      "years", jArr (years |> Seq.map jInt) ]
            ))

let healthJson () = jObj [ "ok", jBool true ]

let identity () =
    jObj
        [ "language", jStr language
          "language_version", jStr languageVersion
          "api_version", jStr apiVersion
          "framework", jStr framework
          "created_year", jInt createdYear
          "schema_version", jInt schemaVersion
          "endpoints", endpoints.DeepClone() ]

let notFoundNode = jObj [ "error", jStr "not_found" ]

let register (port: string) =
    task {
        let url = Environment.GetEnvironmentVariable("CAROLINA_URL")
        let token = Environment.GetEnvironmentVariable("POLYGLOT_REGISTER_TOKEN")

        if String.IsNullOrEmpty(url) || String.IsNullOrEmpty(token) then
            return ()
        else
            let baseUrl = env "PUBLIC_BASE_URL" $"http://127.0.0.1:{port}"

            let body =
                (jObj
                    [ "language", jStr language
                      "language_version", jStr languageVersion
                      "api_version", jStr apiVersion
                      "framework", jStr framework
                      "created_year", jInt createdYear
                      "schema_version", jInt schemaVersion
                      "base_url", jStr baseUrl
                      "endpoints", endpoints.DeepClone() ])
                    .ToJsonString()

            use client = new HttpClient()
            client.Timeout <- TimeSpan.FromSeconds(5.0)

            use req =
                new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/internal/api-endpoints/register")

            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            req.Content <- new StringContent(body, Encoding.UTF8, "application/json")

            try
                let! resp = client.SendAsync(req)
                eprintfn "registered with elixir: %s" (string resp.StatusCode)
            with ex ->
                eprintfn "register: %s" ex.Message
    }

let allDigits (s: string) =
    s.Length > 0 && s |> Seq.forall Char.IsDigit

let handle (path: string) (yearQ: string) : int * JsonNode =
    let parts =
        path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

    try
        match parts with
        | [] -> 200, identity ()
        | [ "health" ] -> 200, healthJson ()
        | [ "v1"; "years" ] ->
            let rows =
                query "SELECT year, slug, name, status FROM v1_years ORDER BY year DESC" [] (fun r ->
                    jObj
                        [ "year", jInt (intCol r "year")
                          "slug", jStr (strOpt r "slug")
                          "name", jStr (strOpt r "name")
                          "status", jStr (strOpt r "status") ])

            200, jObj [ "data", jArr rows ]
        | [ "v1"; "speakers" ] when String.IsNullOrEmpty(yearQ) ->
            let rows =
                query $"SELECT {speakerCols} FROM v1_speakers ORDER BY last_name, first_name" [] speakerObj

            200, jObj [ "data", jArr rows ]
        | [ "v1"; "speakers" ] ->
            let year = int yearQ
            200, jObj [ "data", jArr (listSpeakersYear year) ]
        | [ "v1"; "speakers"; year; slug ] when allDigits year ->
            let y = int year

            match queryOne $"SELECT {speakerCols} FROM v1_speakers WHERE slug = $1" [ box slug ] speakerObj with
            | None -> 404, notFoundNode
            | Some speaker ->
                let talks, langs, topics = loadTalks slug (Some y)

                if List.isEmpty talks then
                    404, notFoundNode
                else
                    let years = talkYears slug

                    200,
                    jObj
                        [ "data",
                          merge
                              speaker
                              [ "year", jInt y
                                "years", jArr (years |> Seq.map jInt)
                                "other_years", jArr (exceptYear years y |> Seq.map jInt)
                                "talks", jArr talks
                                "languages", jArr (langs |> Seq.map jStr)
                                "topics", jArr (topics |> Seq.map jStr) ] ]
        | [ "v1"; "speakers"; slug ] ->
            match queryOne $"SELECT {speakerCols} FROM v1_speakers WHERE slug = $1" [ box slug ] speakerObj with
            | None -> 404, notFoundNode
            | Some speaker ->
                let talks, _, _ = loadTalks slug None
                let years = talkYears slug
                200, jObj [ "data", merge speaker [ "talks", jArr talks; "years", jArr (years |> Seq.map jInt) ] ]
        | [ "v1"; "sponsors" ] when String.IsNullOrEmpty(yearQ) ->
            let rows =
                query $"SELECT {sponsorCols} FROM v1_sponsors ORDER BY name" [] sponsorObj

            200, jObj [ "data", jArr rows ]
        | [ "v1"; "sponsors" ] ->
            let year = int yearQ

            let rows =
                query
                    $"SELECT {yearSponsorCols} FROM v1_year_sponsors WHERE year = $1 ORDER BY name"
                    [ box year ]
                    yearSponsorObj

            200, jObj [ "data", jArr rows ]
        | [ "v1"; "sponsors"; year; slug ] when allDigits year ->
            let y = int year

            match
                queryOne
                    $"SELECT {yearSponsorCols} FROM v1_year_sponsors WHERE year = $1 AND slug = $2"
                    [ box y; box slug ]
                    yearSponsorObj
            with
            | None -> 404, notFoundNode
            | Some sponsor ->
                let years = sponsorYears slug

                200,
                jObj
                    [ "data",
                      merge
                          sponsor
                          [ "years", jArr (years |> Seq.map jInt)
                            "other_years", jArr (exceptYear years y |> Seq.map jInt) ] ]
        | [ "v1"; "sponsors"; slug ] ->
            match queryOne $"SELECT {sponsorCols} FROM v1_sponsors WHERE slug = $1" [ box slug ] sponsorObj with
            | None -> 404, notFoundNode
            | Some sponsor -> 200, jObj [ "data", merge sponsor [ "sponsorships", jArr (loadSponsorships slug) ] ]
        | _ -> 404, notFoundNode
    with ex ->
        500, jObj [ "error", jStr ex.Message ]

type HttpReply =
    { Status: int
      Body: string option
      ContentType: string
      Headers: (string * string) list }

let dispatch (method: string) (path: string) (yearQ: string) =
    let headers = [ "X-Polyglot-Language", language; "X-Polyglot-Framework", framework ]

    if method <> HttpMethods.Get && method <> HttpMethods.Head then
        { Status = 405
          Body = Some """{"error":"method_not_allowed"}"""
          ContentType = "application/json"
          Headers = headers }
    else
        let status, node = handle path yearQ

        { Status = status
          Body = if method = HttpMethods.Head then None else Some(node.ToJsonString())
          ContentType = "application/json"
          Headers = headers }

[<EntryPoint>]
let main args =
    ignore args
    let port = env "PORT" "4010"
    // Listen first. The catalog opens on the first query so /health answers while Postgres is down.
    // Registration starts only after Kestrel is up and must not block the accept loop.
    configureCatalog (defaultDsn ())

    let builder = WebApplication.CreateSlimBuilder(args)
    builder.WebHost.PreferHostingUrls(true) |> ignore
    builder.WebHost.UseUrls(listenUrl port) |> ignore
    let app = builder.Build()

    // Run(handler) only installs terminal middleware. The parameterless Run starts Kestrel.
    app.Run(
        RequestDelegate(fun ctx ->
            task {
                let method = ctx.Request.Method

                let path = if isNull ctx.Request.Path.Value then "/" else ctx.Request.Path.Value

                let mutable yearValues = ctx.Request.Query["year"]
                let yearQ = yearValues.ToString()
                let reply = dispatch method path yearQ

                for key, value in reply.Headers do
                    ctx.Response.Headers[key] <- value

                ctx.Response.StatusCode <- reply.Status
                ctx.Response.ContentType <- reply.ContentType

                match reply.Body with
                | Some body -> do! ctx.Response.WriteAsync(body)
                | None -> ()
            }
            :> Task)
    )

    app.Lifetime.ApplicationStarted.Register(fun () ->
        register port |> ignore
        eprintfn "carolina-codes-fsharp listening on :%s" port)
    |> ignore

    app.Run()
    0
