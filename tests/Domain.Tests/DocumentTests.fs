/// Pure decision/fold tests for the Document aggregate — no actor system, db, or
/// async. The real business rules tested with plain function calls.
module DocumentTests

open System
open Expecto
open FCQRS.Common
open Values
open Helpers

let private now = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

let tests =
    testList
        "Document"
        [ test "create on empty state persists CreateOrUpdateRequested" {
              let d = docRoot (Guid.NewGuid()) "Title" "Body"
              let owner = username "alice"
              let action = Document.decide (command now (Document.CreateOrUpdate(d, owner))) Document.initial
              Expect.equal action (PersistEvent(Document.CreateOrUpdateRequested(d, owner, 1L))) "persist requested"
          }

          test "edit with matching id persists Updated" {
              let id = Guid.NewGuid()
              let state = { Document.initial with Document = Some(docRoot id "T" "B"); Version = 1L }
              let edited = docRoot id "New" "New body"
              let action = Document.decide (command now (Document.CreateOrUpdate(edited, username "alice"))) state
              Expect.equal action (PersistEvent(Document.Updated(edited, 2L))) "persist updated"
          }

          test "command for a different id defers DocumentNotFound" {
              let state = { Document.initial with Document = Some(docRoot (Guid.NewGuid()) "T" "B"); Version = 1L }
              let action =
                  Document.decide (command now (Document.CreateOrUpdate(docRoot (Guid.NewGuid()) "X" "Y", username "alice"))) state
              Expect.equal action (DeferEvent(Document.Errored Document.DocumentNotFound)) "defer error"
          }

          test "approve is idempotent: already Approved -> Defer" {
              let id = Guid.NewGuid()
              let state =
                  { Document.initial with Document = Some(docRoot id "T" "B"); Version = 1L; Approval = Document.Approved }
              let action = Document.decide (command now Document.Approve) state
              Expect.equal action (DeferEvent(Document.ApprovedEvt(DocumentId.OfGuid id))) "defer when already approved"
          }

          test "hold on a pending doc persists HeldForApproval" {
              let id = Guid.NewGuid()
              let state = { Document.initial with Document = Some(docRoot id "T" "B"); Version = 1L }
              let action = Document.decide (command now Document.Hold) state
              Expect.equal action (PersistEvent(Document.HeldForApproval(DocumentId.OfGuid id))) "persist held"
          }

          test "fold CreateOrUpdateRequested stores doc, bumps version, pending" {
              let d = docRoot (Guid.NewGuid()) "T" "B"
              let state = Document.fold (event 1L now (Document.CreateOrUpdateRequested(d, username "alice", 1L))) Document.initial
              Expect.equal state.Document (Some d) "doc stored"
              Expect.equal state.Version 1L "version 1"
              Expect.equal state.Approval Document.Pending "pending"
          }

          test "fold Updated keeps approval, bumps version" {
              let id = Guid.NewGuid()
              let before =
                  { Document.initial with Document = Some(docRoot id "T" "B"); Version = 1L; Approval = Document.Approved }
              let after = Document.fold (event 2L now (Document.Updated(docRoot id "T2" "B2", 2L))) before
              Expect.equal after.Version 2L "version 2"
              Expect.equal after.Approval Document.Approved "approval kept"
          } ]
