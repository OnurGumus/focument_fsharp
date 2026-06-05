/// Integration through the real stack: both aggregates, the quota saga, the
/// projection — booted once over a throwaway SQLite db via the same App.build the
/// web host uses, then driven through the Endpoints instance with a fake
/// HttpContext (as the C# ProjectionIntegrationTests does).
///
/// One actor system is shared across the cases (testSequenced) — booting several
/// in one process would clash on the cluster name / journal. Each case uses fresh
/// usernames so their quotas don't interfere.
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
    App.build (ConfigurationBuilder().Build()) NullLoggerFactory.Instance conn, conn

let private app = lazy (boot ())

let private formCtx (fields: (string * string) list) : HttpContext =
    let ctx = DefaultHttpContext()
    let dict = Dictionary<string, StringValues>()
    for (k, v) in fields do
        dict.[k] <- StringValues v
    ctx.Request.Form <- FormCollection(dict)
    ctx

let private create (e: Endpoints) title user =
    e.CreateOrUpdate(formCtx [ "Title", title; "Content", "body"; "Username", user ]) |> Async.RunSynchronously

let private edit (e: Endpoints) id title content user =
    e.CreateOrUpdate(formCtx [ "Id", id; "Title", title; "Content", content; "Username", user ]) |> Async.RunSynchronously

let private restore (e: Endpoints) id version user =
    e.Restore(formCtx [ "Id", id; "Version", version; "Username", user ]) |> Async.RunSynchronously

let private review approve (e: Endpoints) id user =
    e.Review(approve, formCtx [ "Id", id; "Username", user ]) |> Async.RunSynchronously

let private fillQuota e user =
    for i in 1 .. User.Limit do
        create e (sprintf "doc %d" i) user |> ignore

let private heldDocOf conn user =
    Db.getDocuments conn |> Array.find (fun d -> d.Owner = user && d.ApprovalStatus = "AwaitingApproval")

let private statusOf conn id = (Db.getDocument conn id).Value.ApprovalStatus

let private versionsOf conn id = Db.getDocumentHistory conn id |> Array.map (fun v -> v.Version)

let tests =
    testSequenced
    <| testList
        "integration"
        [ testCase "under quota approved; over quota held; a colleague (not the owner) approves"
          <| fun _ ->
              let endpoints, conn = app.Value
              let alice = freshUser ()
              fillQuota endpoints alice
              Expect.equal (create endpoints "over the limit" alice) "Quota exceeded — sent for approval." "over quota -> held"
              let held = heldDocOf conn alice
              Expect.equal (review true endpoints held.Id alice) "You can't approve your own document." "owner can't approve own"
              Expect.equal (review true endpoints held.Id (freshUser ())) "Approved!" "a colleague approves"
              Expect.equal (statusOf conn held.Id) "Approved" "status flips to Approved"

          testCase "a colleague can reject a held document"
          <| fun _ ->
              let endpoints, conn = app.Value
              let bob = freshUser ()
              fillQuota endpoints bob
              create endpoints "bob over" bob |> ignore
              let held = heldDocOf conn bob
              Expect.equal (review false endpoints held.Id (freshUser ())) "Rejected." "a colleague rejects"
              Expect.equal (statusOf conn held.Id) "Rejected" "status flips to Rejected"

          testCase "edits bump the version and an earlier version can be restored"
          <| fun _ ->
              let endpoints, conn = app.Value
              let dave = freshUser ()
              Expect.equal (create endpoints "v1 title" dave) "Document saved!" "first write saved (under quota)"
              let doc = Db.getDocuments conn |> Array.find (fun d -> d.Owner = dave)

              Expect.equal (edit endpoints doc.Id "v2 title" "v2 body" dave) "Document updated!" "edit -> updated"
              Expect.equal (versionsOf conn doc.Id) [| 2L; 1L |] "two versions after one edit"

              Expect.equal (restore endpoints doc.Id "1" dave) "Version restored!" "restore v1"
              Expect.equal (versionsOf conn doc.Id) [| 3L; 2L; 1L |] "restore appends a new version (never rewrites history)"
              Expect.equal (Db.getDocument conn doc.Id).Value.Title "v1 title" "current content matches the restored v1" ]
