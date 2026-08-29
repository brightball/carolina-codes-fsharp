module Program

open System
open System.Data.Common
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Npgsql

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
    | null | "" -> fallback
    | v -> v

let toNpgsql (dsn: string) =
    if dsn.StartsWith("postgres://") || dsn.StartsWith("postgresql://") then
        let normalized =
            if dsn.StartsWith("postgres://") then "http://" + dsn.Substring("postgres://".Length)
            else "http://" + dsn.Substring("postgresql://".Length)
        let uri = Uri(normalized)
        let userInfo = uri.UserInfo.Split(':')
        let user = if userInfo.Length > 0 then Uri.UnescapeDataString(userInfo[0]) else "postgres"
        let pass =
            if userInfo.Length > 1 then Uri.UnescapeDataString(String.Join(":", userInfo |> Array.skip 1))
            else ""
        let db = uri.AbsolutePath.Trim('/')
        let port = if uri.IsDefaultPort || uri.Port < 0 then 5432 else uri.Port
        $"Host={uri.Host};Port={port};Username={user};Password={pass};Database={db}"
    else
        dsn

let jObj (fields: (string * JsonNode) list) =
    let o = JsonObject()
    for k, v in fields do
        o[k] <- v
    o :> JsonNode

let jArr (xs: JsonNode seq) =
    let a = JsonArray()
    for x in xs do a.Add(x)
    a :> JsonNode

let jNull: JsonNode = JsonValue.Create(null: string) :> JsonNode
let jStr (s: string) = if isNull s then jNull else JsonValue.Create(s) :> JsonNode
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
    if r.IsDBNull(i) then [||]
    else
        match r.GetValue(i) with
        | :? (string[]) as xs -> xs
        | :? Array as arr ->
            [| for x in arr do
                   if not (isNull x) && x <> DBNull.Value then string x |]
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
          "topics", jArr (topics |> Seq.map jStr) ],
    langs,
    topics

let uniq (xs: string seq) =
    let seen = Collections.Generic.HashSet<string>()
    [ for x in xs do
          if not (String.IsNullOrEmpty x) && seen.Add(x) then yield x ]

let loadTalks (conn: NpgsqlConnection) (slug: string) (year: int option) =
    let sql =
        match year with
        | Some _ -> $"SELECT {talkCols} FROM v1_talks WHERE speaker_slug = $1 AND year = $2 ORDER BY year DESC"
        | None -> $"SELECT {talkCols} FROM v1_talks WHERE speaker_slug = $1 ORDER BY year DESC"
    use cmd = new NpgsqlCommand(sql, conn)
    cmd.Parameters.AddWithValue(slug) |> ignore
    match year with
    | Some y -> cmd.Parameters.AddWithValue(y) |> ignore
    | None -> ()
    use r = cmd.ExecuteReader()
    let talks = ResizeArray<JsonNode>()
    let langs = ResizeArray<string>()
    let topics = ResizeArray<string>()
    while r.Read() do
        let t, l, tp = talkObj r
        talks.Add(t)
        langs.AddRange(l)
        topics.AddRange(tp)
    talks |> Seq.toList, uniq langs, uniq topics

let distinctYears (conn: NpgsqlConnection) sql slug =
    use cmd = new NpgsqlCommand(sql, conn)
    cmd.Parameters.AddWithValue(slug) |> ignore
    use r = cmd.ExecuteReader()
    [ while r.Read() do yield r.GetInt32(0) ]

let talkYears conn slug =
    distinctYears conn "SELECT DISTINCT year FROM v1_talks WHERE speaker_slug = $1 ORDER BY year DESC" slug

let sponsorYears conn slug =
    distinctYears conn "SELECT DISTINCT year FROM v1_sponsorships WHERE sponsor_slug = $1 ORDER BY year DESC" slug

let exceptYear years year = years |> List.filter (fun y -> y <> year)

let loadSponsorships (conn: NpgsqlConnection) slug =
    use cmd =
        new NpgsqlCommand(
            "SELECT sponsor_slug, year, tier, blurb, featured FROM v1_sponsorships WHERE sponsor_slug = $1 ORDER BY year DESC",
            conn
        )
    cmd.Parameters.AddWithValue(slug) |> ignore
    use r = cmd.ExecuteReader()
    [ while r.Read() do
          yield
              jObj
                  [ "sponsor_slug", jStr (strOpt r "sponsor_slug")
                    "year", jInt (intCol r "year")
                    "tier", jStr (strOpt r "tier")
                    "blurb", jStr (strOpt r "blurb")
                    "featured", jBool (boolCol r "featured") ] ]

let merge (baseObj: JsonNode) (extra: (string * JsonNode) list) =
    let o = baseObj.AsObject()
    for k, v in extra do o[k] <- v
    o :> JsonNode

let queryRows (conn: NpgsqlConnection) sql (ps: obj list) (map: DbDataReader -> JsonNode) =
    use cmd = new NpgsqlCommand(sql, conn)
    for p in ps do cmd.Parameters.AddWithValue(p) |> ignore
    use r = cmd.ExecuteReader()
    [ while r.Read() do yield map r ]

let queryOne (conn: NpgsqlConnection) sql (ps: obj list) (map: DbDataReader -> JsonNode) =
    use cmd = new NpgsqlCommand(sql, conn)
    for p in ps do cmd.Parameters.AddWithValue(p) |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some(map r) else None

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
                new HttpRequestMessage(
                    HttpMethod.Post,
                    url.TrimEnd('/') + "/internal/api-endpoints/register"
                )
            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
            req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            try
                let! resp = client.SendAsync(req)
                eprintfn "registered with elixir: %s" (string resp.StatusCode)
            with ex ->
                eprintfn "register: %s" ex.Message
    }

let allDigits (s: string) = s.Length > 0 && s |> Seq.forall Char.IsDigit

let handle (ds: NpgsqlDataSource) (path: string) (yearQ: string) : int * JsonNode =
    let parts =
        path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
    try
        match parts with
        | [] -> 200, identity ()
        | [ "health" ] -> 200, jObj [ "ok", jBool true ]
        | [ "v1"; "years" ] ->
            use conn = ds.OpenConnection()
            let rows =
                queryRows conn "SELECT year, slug, name, status FROM v1_years ORDER BY year DESC" [] (fun r ->
                    jObj
                        [ "year", jInt (intCol r "year")
                          "slug", jStr (strOpt r "slug")
                          "name", jStr (strOpt r "name")
                          "status", jStr (strOpt r "status") ])
            200, jObj [ "data", jArr rows ]
        | [ "v1"; "speakers" ] when String.IsNullOrEmpty(yearQ) ->
            use conn = ds.OpenConnection()
            let rows =
                queryRows conn $"SELECT {speakerCols} FROM v1_speakers ORDER BY last_name, first_name" [] speakerObj
            200, jObj [ "data", jArr rows ]
        | [ "v1"; "speakers" ] ->
            use conn = ds.OpenConnection()
            let year = int yearQ
            let speakers =
                queryRows
                    conn
                    $"SELECT {speakerCols} FROM v1_speakers WHERE slug IN (SELECT speaker_slug FROM v1_talks WHERE year = $1) ORDER BY last_name, first_name"
                    [ year ]
                    speakerObj
            let rows =
                [ for sp in speakers do
                      let slug = sp["slug"].GetValue<string>()
                      let talks, langs, topics = loadTalks conn slug (Some year)
                      let years = talkYears conn slug
                      yield
                          merge
                              sp
                              [ "year", jInt year
                                "talks", jArr talks
                                "languages", jArr (langs |> Seq.map jStr)
                                "topics", jArr (topics |> Seq.map jStr)
                                "years", jArr (years |> Seq.map jInt) ] ]
            200, jObj [ "data", jArr rows ]
        | [ "v1"; "speakers"; year; slug ] when allDigits year ->
            use conn = ds.OpenConnection()
            let y = int year
            match queryOne conn $"SELECT {speakerCols} FROM v1_speakers WHERE slug = $1" [ slug ] speakerObj with
            | None -> 404, notFoundNode
            | Some speaker ->
                let talks, langs, topics = loadTalks conn slug (Some y)
                if List.isEmpty talks then
                    404, notFoundNode
                else
                    let years = talkYears conn slug
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
            use conn = ds.OpenConnection()
            match queryOne conn $"SELECT {speakerCols} FROM v1_speakers WHERE slug = $1" [ slug ] speakerObj with
            | None -> 404, notFoundNode
            | Some speaker ->
                let talks, _, _ = loadTalks conn slug None
                let years = talkYears conn slug
                200,
                jObj
                    [ "data",
                      merge
                          speaker
                          [ "talks", jArr talks
                            "years", jArr (years |> Seq.map jInt) ] ]
        | [ "v1"; "sponsors" ] when String.IsNullOrEmpty(yearQ) ->
            use conn = ds.OpenConnection()
            let rows = queryRows conn $"SELECT {sponsorCols} FROM v1_sponsors ORDER BY name" [] sponsorObj
            200, jObj [ "data", jArr rows ]
        | [ "v1"; "sponsors" ] ->
            use conn = ds.OpenConnection()
            let year = int yearQ
            let rows =
                queryRows
                    conn
                    $"SELECT {yearSponsorCols} FROM v1_year_sponsors WHERE year = $1 ORDER BY name"
                    [ year ]
                    yearSponsorObj
            200, jObj [ "data", jArr rows ]
        | [ "v1"; "sponsors"; year; slug ] when allDigits year ->
            use conn = ds.OpenConnection()
            let y = int year
            match
                queryOne
                    conn
                    $"SELECT {yearSponsorCols} FROM v1_year_sponsors WHERE year = $1 AND slug = $2"
                    [ y; slug ]
                    yearSponsorObj
            with
            | None -> 404, notFoundNode
            | Some sponsor ->
                let years = sponsorYears conn slug
                200,
                jObj
                    [ "data",
                      merge
                          sponsor
                          [ "years", jArr (years |> Seq.map jInt)
                            "other_years", jArr (exceptYear years y |> Seq.map jInt) ] ]
        | [ "v1"; "sponsors"; slug ] ->
            use conn = ds.OpenConnection()
            match queryOne conn $"SELECT {sponsorCols} FROM v1_sponsors WHERE slug = $1" [ slug ] sponsorObj with
            | None -> 404, notFoundNode
            | Some sponsor ->
                200, jObj [ "data", merge sponsor [ "sponsorships", jArr (loadSponsorships conn slug) ] ]
        | _ -> 404, notFoundNode
    with
    | ex -> 500, jObj [ "error", jStr ex.Message ]

[<EntryPoint>]
let main args =
    let dsn = toNpgsql (env "DATABASE_URL" "postgres://postgres:postgres@127.0.0.1:5432/carolina_dev")
    let port = env "PORT" "4010"
    let dataSource = NpgsqlDataSource.Create(dsn)

    let builder = WebApplication.CreateBuilder(args)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}") |> ignore
    let app = builder.Build()

    let dispatcher =
        RequestDelegate(fun ctx ->
            task {
                ctx.Response.Headers["X-Polyglot-Language"] <- language
                ctx.Response.Headers["X-Polyglot-Framework"] <- framework
                let method = ctx.Request.Method
                if method <> HttpMethods.Get && method <> HttpMethods.Head then
                    ctx.Response.StatusCode <- 405
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync("""{"error":"method_not_allowed"}""")
                else
                    let path = if isNull ctx.Request.Path.Value then "/" else ctx.Request.Path.Value
                    let yearQ = ctx.Request.Query["year"].ToString()
                    let status, node = handle dataSource path yearQ
                    ctx.Response.StatusCode <- status
                    ctx.Response.ContentType <- "application/json"
                    if method <> HttpMethods.Head then
                        do! ctx.Response.WriteAsync(node.ToJsonString())
            } :> Task
        )

    app.Run(dispatcher)
    register port |> ignore
    eprintfn "carolina-codes-fsharp listening on :%s" port
    app.Run()
    0
