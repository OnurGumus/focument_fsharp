/// Integration through the real stack: both aggregates, the quota saga, the
/// projection — booted over a throwaway SQLite db via the same App.build the web
/// host uses, then driven through the Endpoints instance with a fake HttpContext
/// (as the C# ProjectionIntegrationTests does).
module IntegrationTests

open System
open System.IO
open System.Collections.Generic
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open Focument

let private freshUser () = "u" + Guid.NewGuid().ToString("N").[..7]

let private boot () =
    let dbPath = Path.Combine(Path.GetTempPath(), sprintf "foc_fs_test_%s.db" (Guid.NewGuid().ToString("N")))
    let conn = sprintf "Data Source=%s;" dbPath
    let endpoints = App.build (ConfigurationBuilder().Build()) NullLoggerFactory.Instance conn
    endpoints, conn

/// A POST request whose form holds the given fields.
let private formCtx (fields: (string * string) list) : HttpContext =
    let ctx = DefaultHttpContext()
    let dict = Dictionary<string, StringValues>()
    for (k, v) in fields do
        dict.[k] <- StringValues v
    ctx.Request.Form <- FormCollection(dict)
    ctx

let private create (endpoints: Endpoints) title user =
    endpoints.CreateOrUpdate(formCtx [ "Title", title; "Content", "body"; "Username", user ])
    |> Async.RunSynchronously

let private review (endpoints: Endpoints) id user =
    endpoints.Review(true, formCtx [ "Id", id; "Username", user ]) |> Async.RunSynchronously

let tests =
    testList
        "integration"
        [ testCase "under quota approved; over quota held; a colleague (not the owner) can approve"
          <| fun _ ->
              let endpoints, conn = boot ()
              let alice = freshUser ()

              for i in 1 .. User.Limit do
                  Expect.equal (create endpoints (sprintf "doc %d" i) alice) "Document saved!" "under quota -> saved"

              Expect.equal (create endpoints "over the limit" alice) "Quota exceeded — sent for approval." "over quota -> held"

              let held = Db.getDocuments conn |> Array.find (fun d -> d.ApprovalStatus = "AwaitingApproval")
              Expect.equal (review endpoints held.Id alice) "You can't approve your own document." "owner can't approve own"
              Expect.equal (review endpoints held.Id (freshUser ())) "Approved!" "a colleague approves" ]
