/// The projection: folds the Document event stream into the SQLite read model,
/// advancing the offset in the same transaction. Returns Publish to re-publish the
/// event to subscribers (read-your-writes), or Suppress to update silently — the
/// initial pending request is suppressed; only terminal events wake the web.
/// Mirrors the C# Projection.HandleEventWrapper.
module Projection

open System
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging
open Dapper
open FCQRS.Common
open FCQRS.Model.Data
open Values

let private exec (conn: SqliteConnection) (tx: SqliteTransaction) sql param =
    conn.Execute(sql, param, tx) |> ignore

let handle (loggerFactory: ILoggerFactory) connString offsetValue (eventObj: obj) =
    let log = loggerFactory.CreateLogger "Projection"
    use conn = new SqliteConnection(connString)
    conn.Open()
    use tx = conn.BeginTransaction()
    let mutable verdict = Suppress

    match eventObj with
    | :? (Event<Document.Event>) as docEvent ->
        let now = docEvent.CreationDate.ToString("o")

        match docEvent.EventDetails with
        // A brand-new pending document (creation only). Records the owner + v1.
        // NOT notified — the web waits for the saga's terminal verdict below.
        | Document.CreateOrUpdateRequested(doc, owner, version) ->
            let id = doc.Id.ToString()
            let title: string = ValueLens.Value doc.Title
            let body: string = ValueLens.Value doc.Content
            exec conn tx
                """
                INSERT INTO Documents (Id, Title, Body, Version, CreatedAt, UpdatedAt, ApprovalStatus, Owner)
                VALUES (@Id, @Title, @Body, @Version, @Now, @Now, 'Pending', @Owner)
                """
                ({| Id = id; Title = title; Body = body; Version = version; Now = now; Owner = (ValueLens.Value owner: string) |} :> obj)
            exec conn tx
                "INSERT OR IGNORE INTO DocumentVersions (Id, Version, Title, Body, CreatedAt) VALUES (@Id, @Version, @Title, @Body, @Now)"
                ({| Id = id; Version = version; Title = title; Body = body; Now = now |} :> obj)
            log.LogInformation("projected {Id} v{Version} (pending) at offset {Offset}", id, version, offsetValue)

        // A plain edit — new content/version; approval status + owner kept.
        | Document.Updated(doc, version) ->
            let id = doc.Id.ToString()
            let title: string = ValueLens.Value doc.Title
            let body: string = ValueLens.Value doc.Content
            exec conn tx
                "UPDATE Documents SET Title = @Title, Body = @Body, Version = @Version, UpdatedAt = @Now WHERE Id = @Id"
                ({| Id = id; Title = title; Body = body; Version = version; Now = now |} :> obj)
            exec conn tx
                "INSERT OR IGNORE INTO DocumentVersions (Id, Version, Title, Body, CreatedAt) VALUES (@Id, @Version, @Title, @Body, @Now)"
                ({| Id = id; Version = version; Title = title; Body = body; Now = now |} :> obj)
            verdict <- Publish

        // Terminal verdicts — flip the approval status.
        | Document.ApprovedEvt docId ->
            exec conn tx "UPDATE Documents SET ApprovalStatus = 'Approved', UpdatedAt = @Now WHERE Id = @Id" ({| Id = docId.ToString(); Now = now |} :> obj)
            verdict <- Publish
        | Document.HeldForApproval docId ->
            exec conn tx "UPDATE Documents SET ApprovalStatus = 'AwaitingApproval', UpdatedAt = @Now WHERE Id = @Id" ({| Id = docId.ToString(); Now = now |} :> obj)
            verdict <- Publish
        | Document.RejectedEvt docId ->
            exec conn tx "UPDATE Documents SET ApprovalStatus = 'Rejected', UpdatedAt = @Now WHERE Id = @Id" ({| Id = docId.ToString(); Now = now |} :> obj)
            verdict <- Publish
        | Document.Errored _ -> ()
    | _ -> ()

    exec conn tx "UPDATE Offsets SET OffsetCount = @Offset WHERE OffsetName = 'DocumentProjection'" ({| Offset = offsetValue |} :> obj)
    tx.Commit()
    verdict
