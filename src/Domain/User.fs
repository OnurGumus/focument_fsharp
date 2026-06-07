/// The User aggregate: a per-user sliding-window quota. Mirrors the C# UserShard.
/// The decision uses the command's CreationDate; the fold uses only the event's
/// carried timestamp, so replay is deterministic. Idempotent per document id, so
/// the saga's at-least-once retries can't double-spend a slot.
module User

open FCQRS.Common
open Values
open System

type Command = ConsumeQuota of DocumentId

type Event =
    | QuotaApproved of DocumentId * System.DateTime
    | QuotaRejected

type Consumption = { DocId: DocumentId; At: System.DateTime }

type State = { Consumed: Consumption list }

let initial = { Consumed = [] }

[<Literal>]
let Limit = 3

let Window = TimeSpan.FromMinutes 1.0

let private prune (reference: DateTime) slots =
    let cutoff = reference - Window
    slots |> List.filter (fun c -> c.At > cutoff)

let decide (cmd: Command<_>) state=
    match cmd.CommandDetails with
    | ConsumeQuota docId ->
        match state.Consumed |> List.tryFind (fun c -> c.DocId = docId) with
        // Re-delivery: this document already holds a slot — re-grant the SAME slot.
        // An idempotent no-op on state, so Defer: re-deliver to the saga without
        // journaling a duplicate the fold would only ignore.
        | Some existing -> QuotaApproved(docId, existing.At) |> DeferEvent
        | None ->
            // Only slots within the window count.
            if prune cmd.CreationDate state.Consumed |> List.length < Limit then
                QuotaApproved(docId, cmd.CreationDate) |> PersistEvent
            else
                QuotaRejected |> DeferEvent

let fold evt state =
    match evt.EventDetails with
    // Idempotent on document id (saga crash-recovery safe).
    | QuotaApproved(docId, _) when state.Consumed |> List.exists (fun c -> c.DocId = docId) -> state
    | QuotaApproved(docId, at) -> { state with Consumed = prune at ({ DocId = docId; At = at } :: state.Consumed) }
    | QuotaRejected -> state
