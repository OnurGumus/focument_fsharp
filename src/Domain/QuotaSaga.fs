/// The quota saga: the one piece that spans two aggregates. It starts from the
/// Document's CreateOrUpdateRequested, asks the User aggregate to consume a quota
/// slot, then tells the Document to Approve (quota ok) or Hold (over quota, park
/// for a colleague). Mirrors the C# QuotaSaga.
///
/// The saga sees events as obj (they come from BOTH the Document originator and the
/// User aggregate); these active patterns recover the typed payload so the handler
/// can match event + state in one flat pass.
module QuotaSaga

open FCQRS.Common
open FCQRS.FSharp
open Values

// No cross-step data is needed (the saga's progress lives entirely in its state),
// so its data type is just unit — written as `_` wherever it appears below.
type State =
    | CheckingQuota of Username * DocumentId
    | Approving of DocumentId
    | Holding of DocumentId
    | Done

let private (|DocEvent|_|) (o: obj) =
    match o with
    | :? (Event<Document.Event>) as e -> Some e.EventDetails
    | _ -> None

let private (|UserEvent|_|) (o: obj) =
    match o with
    | :? (Event<User.Event>) as e -> Some e.EventDetails
    | _ -> None

let private handleEvent evt sagaState =
    match evt, sagaState.State with
    | DocEvent(Document.CreateOrUpdateRequested(doc, owner)), None -> CheckingQuota(owner, doc.Id) |> StateChangedEvent
    | UserEvent(User.QuotaApproved _), Some(CheckingQuota(_, docId)) -> Approving docId |> StateChangedEvent
    | UserEvent User.QuotaRejected, Some(CheckingQuota(_, docId)) -> Holding docId |> StateChangedEvent
    | DocEvent(Document.ApprovedEvt _), Some(Approving _) -> Done |> StateChangedEvent
    | DocEvent(Document.HeldForApproval _), Some(Holding _) -> Done |> StateChangedEvent
    | _ -> UnhandledEvent

let private applySideEffects documentFactory userFactory sagaState _recovering =
    match sagaState.State with
    // Ask the User aggregate (keyed by the owner's username) to consume a slot.
    | CheckingQuota(owner, docId) -> Stay, [ toAggregate userFactory owner.Value (User.ConsumeQuota docId) ]
    // Quota ok -> tell the originating Document to approve.
    | Approving _ -> Stay, [ toOriginator documentFactory Document.Approve ]
    // Over quota -> park for a colleague.
    | Holding _ -> Stay, [ toOriginator documentFactory Document.Hold ]
    | Done -> StopSaga, []

let startsOn evt =
    match evt with
    | DocEvent(Document.CreateOrUpdateRequested _) -> true
    | _ -> false

/// Build the saga definition once the two aggregates' factories are known
/// (supplied by the composition root after the aggregates are registered).
let definition documentFactory userFactory =
    { Name = "QuotaSaga"
      InitialData = ()
      Originator = documentFactory
      HandleEvent = handleEvent
      ApplySideEffects = applySideEffects documentFactory userFactory
      StartOn = startsOn }
