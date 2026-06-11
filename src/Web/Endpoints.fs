// The HTTP handlers as an instance class — the same read-your-writes flow as the
// C# Endpoints. The constructor captures the cross-cutting dependencies once (the
// read-model connection, the projection subscription and the Document aggregate
// handle), so each method takes only the request.
//
// The write handlers are `asyncResult` pipelines: each `let!`/`do!` short-circuits
// to an Error message on the first failed step, so the happy path reads top to
// bottom with no nesting. `Reply.respond` collapses the Result back to the single
// text response the endpoint returns either way.
namespace Focument

open System
open Microsoft.AspNetCore.Http
open FsToolkit.ErrorHandling
open FCQRS.FSharp
open FCQRS.Model.Data
open Values

module private Reply =
    // Collapse the Result back to the single text response the endpoint returns
    // either way (for these handlers the error message *is* the response body).
    let respond computation =
        async {
            match! computation with
            | Ok message
            | Error message -> return message
        }

type Endpoints
    (connString, subscriptions: FCQRS.Query.ISubscribe, documents) =

    member _.GetDocuments() = Db.getDocuments connString

    member _.GetDocumentHistory(ctx: HttpContext) =
        let id =
            match ctx.Request.RouteValues.TryGetValue "id" with
            | true, v -> string v
            | _ -> ""
        Db.getDocumentHistory connString id

    /// Create a new document (quota-gated via the saga) or edit an existing one.
    member _.CreateOrUpdate(ctx: HttpContext) =
        Reply.respond (
            asyncResult {
                let! f = ctx.Request.ReadFormAsync() |> Async.AwaitTask
                let! (owner: Username) =
                    string f.["Username"]
                    |> ValueLens.TryCreate
                    |> Result.mapError ((+) "Error: ")

                let existingId = f.["Id"].ToString()
                let docId = if String.IsNullOrEmpty existingId then Guid.NewGuid() else Guid.Parse existingId
                let! doc = Document.Root.TryCreate(docId, f.["Title"].ToString(), f.["Content"].ToString()) |> Result.mapError (fun e -> $"Error: {e}")

                let cid = Fcqrs.newCid ()
                let aggId = Fcqrs.aggregateId (string docId)
                // Subscribe before sending so we can wait for the read model to catch
                // up. The projection notifies only terminal events, so one is enough.
                use awaiter = subscriptions.Subscribe(cid, 1)

                let! result =
                    documents.Send cid aggId (Document.CreateOrUpdate(doc, owner)) (fun e ->
                        match e with
                        | Document.ApprovedEvt _
                        | Document.HeldForApproval _
                        | Document.Updated _ -> true
                        | _ -> false)

                do! awaiter.Task |> Async.AwaitTask

                return
                    match result.EventDetails with
                    | Document.HeldForApproval _ -> "Quota exceeded — sent for approval."
                    | Document.Updated _ -> "Document updated!"
                    | _ -> "Document saved!"
            }
        )

    /// Restore an earlier version by re-issuing its content as a plain edit
    /// (Updated) — no quota, no saga.
    member _.Restore(ctx: HttpContext) =
        Reply.respond (
            asyncResult {
                let! f = ctx.Request.ReadFormAsync() |> Async.AwaitTask
                let docId = f.["Id"].ToString()
                let! (owner: Username) =
                    string f.["Username"]
                    |> ValueLens.TryCreate
                    |> Result.mapError ((+) "Error: ")

                let! guid =
                    match Guid.TryParse docId with
                    | true, g -> Ok g
                    | _ -> Error "Error: invalid document id"

                let! version =
                    match Int64.TryParse(f.["Version"].ToString()) with
                    | true, v -> Ok v
                    | _ -> Error "Error: invalid version"

                let! snapshot =
                    Db.getDocumentHistory connString docId
                    |> Array.tryFind (fun v -> v.Version = version)
                    |> Result.requireSome "Error: version not found"

                let! doc = Document.Root.TryCreate(guid, snapshot.Title, snapshot.Body) |> Result.mapError (fun e -> $"Error: {e}")

                let cid = Fcqrs.newCid ()
                let aggId = Fcqrs.aggregateId docId
                use awaiter = subscriptions.Subscribe(cid, 1)

                let! _ =
                    documents.Send cid aggId (Document.CreateOrUpdate(doc, owner)) (fun e ->
                        match e with
                        | Document.Updated _
                        | Document.ApprovedEvt _
                        | Document.HeldForApproval _ -> true
                        | _ -> false)

                do! awaiter.Task |> Async.AwaitTask
                return "Version restored!"
            }
        )

    /// A colleague approves or rejects a held (over-quota) document. The owner
    /// can't decide their own.
    member _.Review(approve, ctx: HttpContext) =
        let verb = if approve then "approve" else "reject"

        Reply.respond (
            asyncResult {
                let! f = ctx.Request.ReadFormAsync() |> Async.AwaitTask
                let docId = f.["Id"].ToString()
                let username = f.["Username"].ToString()

                let! (_: Username) =
                    username
                    |> ValueLens.TryCreate
                    |> Result.mapError ((+) "Error: ")
                do! match Guid.TryParse docId with
                     | true, _ -> Ok()
                     | _ -> Error "Error: invalid document id"

                let! doc = Db.getDocument connString docId |> Result.requireSome "Error: document not found"
                do! if doc.Owner = username then Error $"You can't {verb} your own document." else Ok()

                let cid = Fcqrs.newCid ()
                let aggId = Fcqrs.aggregateId docId
                use awaiter = subscriptions.Subscribe(cid, 1)
                let command = if approve then Document.Approve else Document.Reject

                let! _ =
                    documents.Send cid aggId command (fun e ->
                        match e with
                        | Document.ApprovedEvt _ -> approve
                        | Document.RejectedEvt _ -> not approve
                        | _ -> false)

                do! awaiter.Task |> Async.AwaitTask
                return if approve then "Approved!" else "Rejected."
            }
        )
