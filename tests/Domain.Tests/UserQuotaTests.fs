/// Pure tests for the User aggregate's sliding-window quota, with a
/// FakeTimeProvider so the window + idempotency are deterministic.
module UserQuotaTests

open System
open Expecto
open Microsoft.Extensions.Time.Testing
open FCQRS.Common
open Values
open Helpers

/// Run decide at `now`; if it persists, fold the event into state. Returns the
/// resulting event and the next state.
let private step now docId state =
    match User.decide (command now (User.ConsumeQuota docId)) state with
    | PersistEvent e -> e, User.fold (event 1L now e) state
    | DeferEvent e -> e, state
    | _ -> failtest "unexpected action"

let tests =
    testList
        "User quota"
        [ test "allows up to Limit, then rejects within the window" {
              let tp = FakeTimeProvider(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
              let now () = tp.GetUtcNow().UtcDateTime
              let mutable state = User.initial
              for _ in 1 .. User.Limit do
                  let e, s = step (now ()) (DocumentId.Create ()) state
                  state <- s
                  match e with
                  | User.QuotaApproved _ -> ()
                  | _ -> failtest "should approve under quota"
              let e, _ = step (now ()) (DocumentId.Create ()) state
              match e with
              | User.QuotaRejected -> ()
              | _ -> failtest "should reject over quota"
          }

          test "window slide allows again" {
              let tp = FakeTimeProvider(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
              let mutable state = User.initial
              for _ in 1 .. User.Limit do
                  let _, s = step (tp.GetUtcNow().UtcDateTime) (DocumentId.Create ()) state
                  state <- s
              tp.Advance(User.Window + TimeSpan.FromSeconds 1.0)
              let e, _ = step (tp.GetUtcNow().UtcDateTime) (DocumentId.Create ()) state
              match e with
              | User.QuotaApproved _ -> ()
              | _ -> failtest "should approve after the window slides past old slots"
          }

          test "re-consuming the same document doesn't take a second slot" {
              let tp = FakeTimeProvider(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
              let now = tp.GetUtcNow().UtcDateTime
              let docId = DocumentId.Create ()
              let _, state1 = step now docId User.initial
              // re-deliver the same ConsumeQuota — must re-grant the same slot, and the
              // fold must stay idempotent (no second consumption row).
              let e, state2 = step now docId state1
              match e with
              | User.QuotaApproved _ -> ()
              | _ -> failtest "should re-approve the same slot"
              Expect.equal (List.length state2.Consumed) 1 "still a single consumption"
          } ]
