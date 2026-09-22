module HandlerTests

open System.Text.Json.Nodes
open Xunit

let speakerFields =
    [ "slug"
      "first_name"
      "last_name"
      "name"
      "tagline"
      "bio"
      "company"
      "location"
      "photo_path"
      "twitter_url"
      "linkedin_url"
      "website_url"
      "github_url"
      "featured" ]

let sponsorFields =
    [ "slug"
      "name"
      "website"
      "logo_path"
      "description"
      "twitter_url"
      "linkedin_url"
      "youtube_url"
      "instagram_url"
      "facebook_url" ]

let yearSponsorFields = sponsorFields @ [ "blurb"; "tier"; "featured"; "year" ]

let slugs = [ "ada"; "grace"; "linus"; "edsger"; "barbara" ]

let speakerRow slug =
    Program.jObj
        [ "slug", Program.jStr slug
          "first_name", Program.jStr slug
          "last_name", Program.jStr "Speaker"
          "name", Program.jStr (slug + " Speaker")
          "tagline", Program.jStr "talks"
          "bio", Program.jStr "bio"
          "company", Program.jStr "Carolina"
          "location", Program.jStr "Raleigh"
          "photo_path", Program.jStr ("/photos/" + slug)
          "twitter_url", Program.jStr ("https://twitter.com/" + slug)
          "linkedin_url", Program.jStr ("https://linkedin.com/in/" + slug)
          "website_url", Program.jStr ("https://" + slug + ".example")
          "github_url", Program.jStr ("https://github.com/" + slug)
          "featured", Program.jBool true ]

let talkRow slug year =
    Program.jObj
        [ "slug", Program.jStr (slug + "-talk")
          "title", Program.jStr (slug + " talk")
          "description", Program.jStr "a talk"
          "format", Program.jStr "talk"
          "youtube_id", Program.jStr "abc123"
          "year", Program.jInt year
          "speaker_slug", Program.jStr slug
          "languages", Program.jArr [ Program.jStr "fsharp"; Program.jStr "fsharp"; Program.jStr "sql" ]
          "topics", Program.jArr [ Program.jStr "compilers"; Program.jStr "compilers" ] ]

let sponsorRow slug =
    Program.jObj
        [ "slug", Program.jStr slug
          "name", Program.jStr (slug + " Co")
          "website", Program.jStr ("https://" + slug + ".example")
          "logo_path", Program.jStr ("/logos/" + slug)
          "description", Program.jStr "sponsor"
          "twitter_url", Program.jStr ("https://twitter.com/" + slug)
          "linkedin_url", Program.jStr ("https://linkedin.com/company/" + slug)
          "youtube_url", Program.jStr ("https://youtube.com/" + slug)
          "instagram_url", Program.jStr ("https://instagram.com/" + slug)
          "facebook_url", Program.jStr ("https://facebook.com/" + slug) ]

let yearSponsorRow slug year =
    Program.merge
        (sponsorRow slug)
        [ "blurb", Program.jStr "blurb"
          "tier", Program.jStr "gold"
          "featured", Program.jBool true
          "year", Program.jInt year ]

let sponsorshipRow slug =
    Program.jObj
        [ "sponsor_slug", Program.jStr slug
          "year", Program.jInt 2026
          "tier", Program.jStr "gold"
          "blurb", Program.jStr "blurb"
          "featured", Program.jBool true ]

let yearLink slug year =
    Program.jObj [ "speaker_slug", Program.jStr slug; "year", Program.jInt year ]

let argAt (ps: obj list) index =
    match List.tryItem index ps with
    | Some(:? string as text) -> text
    | Some(:? int as n) -> string n
    | _ -> ""

let catalogHook (sql: string) (ps: obj list) =
    if sql.Contains("FROM v1_years") then
        [ Program.jObj
              [ "year", Program.jInt 2026
                "slug", Program.jStr "2026"
                "name", Program.jStr "Carolina Code Conference 2026"
                "status", Program.jStr "past" ] ]
    elif sql.Contains("FROM v1_speakers WHERE slug IN") then
        slugs |> List.map speakerRow
    elif sql.Contains("FROM v1_speakers WHERE slug =") then
        match argAt ps 0 with
        | "missing" -> []
        | "silent" -> [ speakerRow "silent" ]
        | slug when List.contains slug slugs -> [ speakerRow slug ]
        | _ -> []
    elif sql.Contains("FROM v1_speakers") then
        slugs |> List.map speakerRow
    elif sql.Contains("DISTINCT speaker_slug, year FROM v1_talks") then
        slugs |> List.collect (fun slug -> [ yearLink slug 2026; yearLink slug 2024 ])
    elif sql.Contains("DISTINCT year FROM v1_talks") then
        [ Program.jInt 2026; Program.jInt 2024 ]
    elif sql.Contains("FROM v1_talks WHERE year =") then
        slugs |> List.map (fun slug -> talkRow slug 2026)
    elif sql.Contains("FROM v1_talks WHERE speaker_slug =") && sql.Contains("AND year =") then
        match argAt ps 0 with
        | "silent" -> []
        | slug when List.contains slug slugs -> [ talkRow slug 2026 ]
        | _ -> []
    elif sql.Contains("FROM v1_talks WHERE speaker_slug =") then
        match argAt ps 0 with
        | slug when List.contains slug slugs -> [ talkRow slug 2026; talkRow slug 2024 ]
        | _ -> []
    elif sql.Contains("v1_sponsorships") && sql.Contains("DISTINCT year") then
        [ Program.jInt 2026; Program.jInt 2024 ]
    elif sql.Contains("v1_sponsorships") then
        [ sponsorshipRow (argAt ps 0) ]
    elif sql.Contains("v1_year_sponsors") && sql.Contains("AND slug =") then
        match argAt ps 1 with
        | "missing" -> []
        | slug -> [ yearSponsorRow slug 2026 ]
    elif sql.Contains("v1_year_sponsors") then
        [ yearSponsorRow "acme" 2026 ]
    elif sql.Contains("FROM v1_sponsors WHERE slug =") then
        match argAt ps 0 with
        | "missing" -> []
        | slug -> [ sponsorRow slug ]
    elif sql.Contains("FROM v1_sponsors") then
        [ sponsorRow "acme" ]
    else
        failwithf "unexpected sql: %s" sql

let useCatalog () =
    Program.resetCatalog ()
    Program.queryHook <- Some catalogHook

let body (reply: Program.HttpReply) =
    match reply.Body with
    | Some text -> text
    | None -> failwith "expected a JSON body"

let json (reply: Program.HttpReply) =
    match JsonNode.Parse(body reply) with
    | null -> failwith "empty json"
    | node -> node

let dataArray (reply: Program.HttpReply) =
    match (json reply)["data"] with
    | null -> failwith "missing data"
    | node -> node.AsArray()

let dataObject (reply: Program.HttpReply) =
    match (json reply)["data"] with
    | null -> failwith "missing data"
    | node -> node

let assertFields (node: JsonNode) (names: string list) =
    for name in names do
        if isNull node[name] then
            failwithf "missing field %s in %s" name (node.ToJsonString())

let header (reply: Program.HttpReply) name =
    reply.Headers
    |> List.pick (fun (key, value) -> if key = name then Some value else None)

let assertPolyglot (reply: Program.HttpReply) =
    Assert.Equal("F#", header reply "X-Polyglot-Language")
    Assert.Equal("ASP.NET", header reply "X-Polyglot-Framework")
    Assert.Equal("application/json", reply.ContentType)

[<Fact>]
let ``identity reports F# and ASP.NET without postgres`` () =
    Program.resetCatalog ()
    let reply = Program.dispatch "GET" "/" ""
    Assert.Equal(200, reply.Status)
    assertPolyglot reply
    let node = json reply

    assertFields
        node
        [ "language"
          "language_version"
          "api_version"
          "framework"
          "created_year"
          "schema_version"
          "endpoints" ]

    Assert.Contains("\"language\":\"F#\"", body reply)
    Assert.Contains("\"framework\":\"ASP.NET\"", body reply)
    Assert.Contains("/health", body reply)
    Assert.Equal(0, Program.sqlCount)
    Assert.Equal(0, Program.connectCount)

    match node["endpoints"] with
    | null -> failwith "missing endpoints"
    | endpoints -> Assert.Equal(9, endpoints.AsArray().Count)

[<Fact>]
let ``health is ok without opening postgres`` () =
    Program.resetCatalog ()
    let reply = Program.dispatch "GET" "/health" ""
    Assert.Equal(200, reply.Status)
    assertPolyglot reply
    Assert.Contains("\"ok\":true", body reply)
    Assert.Equal(0, Program.sqlCount)
    Assert.Equal(0, Program.connectCount)

[<Fact>]
let ``years catalog path uses shipped handle without postgres`` () =
    useCatalog ()
    let reply = Program.dispatch "GET" "/v1/years" ""
    Assert.Equal(200, reply.Status)
    assertPolyglot reply
    Assert.Equal(1, Program.sqlCount)
    Assert.Equal(1, Program.connectCount)
    let rows = dataArray reply
    Assert.Equal(1, rows.Count)

    match rows[0] with
    | null -> failwith "missing year row"
    | row ->
        assertFields row [ "year"; "slug"; "name"; "status" ]
        Assert.Contains("Carolina Code Conference 2026", row.ToJsonString())

[<Fact>]
let ``advertised routes return 200 and the handler fields`` () =
    useCatalog ()
    let root = Program.dispatch "GET" "/" ""
    Assert.Equal(0, Program.sqlCount)

    match (json root)["endpoints"] with
    | null -> failwith "missing endpoints"
    | node ->
        let endpoints = node.AsArray()

        for endpoint in endpoints do
            match endpoint with
            | null -> failwith "null endpoint"
            | ep ->
                let path = ep["path"].GetValue<string>()

                let queries =
                    ep["query"].AsArray()
                    |> Seq.choose (fun q -> if isNull q then None else Some(q.GetValue<string>()))
                    |> Seq.toList

                let concrete = path.Replace(":year", "2026").Replace(":slug", "ada")
                let yearQ = if List.contains "year" queries then "2026" else ""
                let before = Program.connectCount
                let reply = Program.dispatch "GET" concrete yearQ
                Assert.True(reply.Status = 200, sprintf "%s year=%s -> %d %s" concrete yearQ reply.Status (body reply))
                assertPolyglot reply
                Assert.False(System.String.IsNullOrEmpty(body reply))
                Assert.True(Program.connectCount <= before + 1)

    let speakers = Program.dispatch "GET" "/v1/speakers" ""
    Assert.Equal(200, speakers.Status)
    let speakerRows = dataArray speakers
    Assert.True(speakerRows.Count >= 3)

    match speakerRows[0] with
    | null -> failwith "missing speaker"
    | row -> assertFields row speakerFields

    let byYear = Program.dispatch "GET" "/v1/speakers" "2026"
    Assert.Equal(200, byYear.Status)
    let yearRows = dataArray byYear
    Assert.True(yearRows.Count >= 3)

    match yearRows[0] with
    | null -> failwith "missing year speaker"
    | row ->
        assertFields row (speakerFields @ [ "year"; "talks"; "languages"; "topics"; "years" ])

        match row["languages"] with
        | null -> failwith "missing languages"
        | langs ->
            Assert.Equal(2, langs.AsArray().Count)
            Assert.Contains("fsharp", langs.ToJsonString())
            Assert.Contains("sql", langs.ToJsonString())

    let one = Program.dispatch "GET" "/v1/speakers/ada" ""
    Assert.Equal(200, one.Status)
    assertFields (dataObject one) (speakerFields @ [ "talks"; "years" ])

    let oneYear = Program.dispatch "GET" "/v1/speakers/2026/ada" ""
    Assert.Equal(200, oneYear.Status)
    let detail = dataObject oneYear

    assertFields
        detail
        (speakerFields
         @ [ "year"; "years"; "other_years"; "talks"; "languages"; "topics" ])

    Assert.Contains("2024", detail["other_years"].ToJsonString())

    let sponsors = Program.dispatch "GET" "/v1/sponsors" ""
    Assert.Equal(200, sponsors.Status)

    match (dataArray sponsors)[0] with
    | null -> failwith "missing sponsor"
    | row -> assertFields row sponsorFields

    let sponsorsYear = Program.dispatch "GET" "/v1/sponsors" "2026"
    Assert.Equal(200, sponsorsYear.Status)

    match (dataArray sponsorsYear)[0] with
    | null -> failwith "missing year sponsor"
    | row -> assertFields row yearSponsorFields

    let sponsor = Program.dispatch "GET" "/v1/sponsors/acme" ""
    Assert.Equal(200, sponsor.Status)
    assertFields (dataObject sponsor) (sponsorFields @ [ "sponsorships" ])

    let sponsorYear = Program.dispatch "GET" "/v1/sponsors/2026/acme" ""
    Assert.Equal(200, sponsorYear.Status)
    let sponsorDetail = dataObject sponsorYear
    assertFields sponsorDetail (yearSponsorFields @ [ "years"; "other_years" ])
    Assert.Contains("2024", sponsorDetail["other_years"].ToJsonString())

[<Fact>]
let ``missing speaker or sponsor resources and unknown paths are not found`` () =
    useCatalog ()

    for path in
        [ "/v1/speakers/missing"
          "/v1/speakers/2026/missing"
          "/v1/speakers/2026/silent"
          "/v1/sponsors/missing"
          "/v1/sponsors/2026/missing"
          "/no-such"
          "/v1/unknown" ] do
        let reply = Program.dispatch "GET" path ""
        Assert.Equal(404, reply.Status)
        assertPolyglot reply
        Assert.Equal("""{"error":"not_found"}""", body reply)

[<Fact>]
let ``non-GET returns method_not_allowed and does not touch the catalog`` () =
    Program.resetCatalog ()

    for method in [ "POST"; "PUT"; "DELETE" ] do
        let reply = Program.dispatch method "/health" ""
        Assert.Equal(405, reply.Status)
        assertPolyglot reply
        Assert.Equal("""{"error":"method_not_allowed"}""", body reply)

    let catalog = Program.dispatch "POST" "/v1/speakers" "2026"
    Assert.Equal(405, catalog.Status)
    Assert.Equal("""{"error":"method_not_allowed"}""", body catalog)
    Assert.Equal(0, Program.sqlCount)
    Assert.Equal(0, Program.connectCount)

[<Fact>]
let ``HEAD keeps the status and omits the body`` () =
    Program.resetCatalog ()
    let reply = Program.dispatch "HEAD" "/health" ""
    Assert.Equal(200, reply.Status)
    assertPolyglot reply
    Assert.Equal(None, reply.Body)
    Assert.Equal(0, Program.sqlCount)
    Assert.Equal(0, Program.connectCount)
