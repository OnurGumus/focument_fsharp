/// The Document aggregate: pure decide (handleCommand) + fold (applyEvent).
/// Mirrors the C# DocumentShard. A first write records a pending request that
/// starts the quota saga; edits skip the saga; verdict commands are idempotent.
module Document

open FCQRS.Common
open FCQRS.Model.Data
open Values

/// The document content as carried on the wire (validated).
type Root =
    { Id: DocumentId; Title: Title; Content: Content }

    static member TryCreate(guid, title, content) =
        match (ValueLens.TryCreate title: Result<Title, _>), (ValueLens.TryCreate content: Result<Content, _>) with
        | Ok t, Ok c -> Ok { Id = DocumentId.OfGuid guid; Title = t; Content = c }
        | Error e, _ -> Error e
        | _, Error e -> Error e

/// Where the document sits in the quota workflow.
type Approval =
    | Pending
    | AwaitingApproval
    | Approved
    | Rejected

type DocumentError = DocumentNotFound

type Command =
    | CreateOrUpdate of Root * Username   // owner
    | Approve
    | Reject
    | Hold

type Event =
    | CreateOrUpdateRequested of Root * Username
    | Updated of Root
    | Errored of DocumentError
    | ApprovedEvt of DocumentId
    | RejectedEvt of DocumentId
    | HeldForApproval of DocumentId

type State =
    { Document: Root option
      Version: int64
      Approval: Approval }

let initial = { Document = None; Version = 0L; Approval = Pending }

/// decide: command + current state -> what to do.
let decide (cmd: Command<_>) state =
    match cmd.CommandDetails, state.Document with
    // First write — no document yet. Pending request that starts the quota saga.
    | CreateOrUpdate(doc, owner), None -> CreateOrUpdateRequested(doc, owner) |> PersistEvent
    // Edit of the document we already hold — no saga, no quota.
    | CreateOrUpdate(doc, _), Some existing when existing.Id = doc.Id -> Updated doc |> PersistEvent
    // Wrong id routed here.
    | CreateOrUpdate _, _ -> Errored DocumentNotFound |> DeferEvent
    // Verdicts — idempotent: if already in the target state, defer (still published
    // so a re-issuing saga sees it) rather than persist a duplicate.
    | Approve, Some doc ->
        let e = ApprovedEvt doc.Id
        if state.Approval = Approved then DeferEvent e else PersistEvent e
    | Reject, Some doc ->
        let e = RejectedEvt doc.Id
        if state.Approval = Rejected then DeferEvent e else PersistEvent e
    | Hold, Some doc ->
        let e = HeldForApproval doc.Id
        if state.Approval = AwaitingApproval then DeferEvent e else PersistEvent e
    | _ -> UnhandledEvent

/// fold: event + current state -> next state (pure).
let fold evt state =
    match evt.EventDetails with
    | CreateOrUpdateRequested(doc, _) -> { state with Document = Some doc; Version = state.Version + 1L; Approval = Pending }
    | Updated doc -> { state with Document = Some doc; Version = state.Version + 1L }
    | ApprovedEvt _ -> { state with Approval = Approved }
    | RejectedEvt _ -> { state with Approval = Rejected }
    | HeldForApproval _ -> { state with Approval = AwaitingApproval }
    | Errored _ -> state
