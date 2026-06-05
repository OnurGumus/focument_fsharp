/// The projection: folds the Document event stream into the SQLite read model,
/// advancing the offset in the same transaction. Returns the events to re-publish
/// to subscribers (read-your-writes) — only terminal events, never the initial
/// pending request. Mirrors the C# Projection.HandleEventWrapper.
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
    let mutable notify : IMessageWithCID list = []

    match eventObj with
    | :? (Event<Document.Event>) as docEvent ->
        let now = docEvent.CreationDate.ToString("o")

        match docEvent.EventDetails with
        // A brand-new pending document (creation only). Records the owner + v1.
        // NOT notified — the web waits for the saga's terminal verdict below.
        | Document.CreateOrUpdateRequested(doc, owner) ->
            let id = doc.Id.ToString()
            let title = doc.Title.Value
            let body = doc.Content.Value
            exec conn tx
                """
                INSERT INTO Documents (Id, Title, Body, Version, CreatedAt, UpdatedAt, ApprovalStatus, Owner)
                VALUES (@Id, @Title, @Body, 1, @Now, @Now, 'Pending', @Owner)
                """
                ({| Id = id; Title = title; Body = body; Now = now; Owner = owner.Value |} :> obj)
            exec conn tx
                "INSERT OR IGNORE INTO DocumentVersions (Id, Version, Title, Body, CreatedAt) VALUES (@Id, 1, @Title, @Body, @Now)"
                ({| Id = id; Title = title; Body = body; Now = now |} :> obj)
            log.LogInformation("projected {Id} v1 (pending) at offset {Offset}", id, offsetValue)

        // A plain edit — new content/version; approval status + owner kept.
        | Document.Updated doc ->
            let id = doc.Id.ToString()
            let title = doc.Title.Value
            let body = doc.Content.Value
            let maxV = conn.ExecuteScalar("SELECT COALESCE(MAX(Version), 0) FROM DocumentVersions WHERE Id = @Id", ({| Id = id |} :> obj), tx) |> Convert.ToInt64
            let version = maxV + 1L
            exec conn tx
                "UPDATE Documents SET Title = @Title, Body = @Body, Version = @Version, UpdatedAt = @Now WHERE Id = @Id"
                ({| Id = id; Title = title; Body = body; Version = version; Now = now |} :> obj)
            exec conn tx
                "INSERT OR IGNORE INTO DocumentVersions (Id, Version, Title, Body, CreatedAt) VALUES (@Id, @Version, @Title, @Body, @Now)"
                ({| Id = id; Version = version; Title = title; Body = body; Now = now |} :> obj)
            notify <- [ docEvent ]

        // Terminal verdicts — flip the approval status.
        | Document.ApprovedEvt docId ->
            exec conn tx "UPDATE Documents SET ApprovalStatus = 'Approved', UpdatedAt = @Now WHERE Id = @Id" ({| Id = docId.ToString(); Now = now |} :> obj)
            notify <- [ docEvent ]
        | Document.HeldForApproval docId ->
            exec conn tx "UPDATE Documents SET ApprovalStatus = 'AwaitingApproval', UpdatedAt = @Now WHERE Id = @Id" ({| Id = docId.ToString(); Now = now |} :> obj)
            notify <- [ docEvent ]
        | Document.RejectedEvt docId ->
            exec conn tx "UPDATE Documents SET ApprovalStatus = 'Rejected', UpdatedAt = @Now WHERE Id = @Id" ({| Id = docId.ToString(); Now = now |} :> obj)
            notify <- [ docEvent ]
        | Document.Errored _ -> ()
    | _ -> ()

    exec conn tx "UPDATE Offsets SET OffsetCount = @Offset WHERE OffsetName = 'DocumentProjection'" ({| Offset = offsetValue |} :> obj)
    tx.Commit()
    notify
