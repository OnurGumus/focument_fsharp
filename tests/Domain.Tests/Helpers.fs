/// Test helpers: build the Command/Event envelopes the pure decide/fold functions
/// expect (the framework builds these at runtime; tests build them by hand), plus
/// small value-object constructors.
module Helpers

open System
open FCQRS.Common
open FCQRS.Model.Data
open Values

// The CreateAsResult target can't be inferred, so these two keep their annotation.
let private newMessageId () : MessageId =
    Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value

let private newCid () : CID =
    Guid.CreateVersion7().ToString() |> ValueLens.CreateAsResult |> Result.value

/// Wrap a command payload, stamping CreationDate (used by the quota window).
let command at details =
    { CommandDetails = details
      CreationDate = at
      Id = newMessageId ()
      Sender = None
      CorrelationId = newCid ()
      Metadata = Map.empty }

/// Wrap an event payload carrying a version + timestamp.
let event version at details =
    { EventDetails = details
      CreationDate = at
      Id = newMessageId ()
      Sender = None
      CorrelationId = newCid ()
      Version = ValueLens.TryCreate version |> Result.value
      Metadata = Map.empty }

let title t = match Title.TryCreate t with Ok x -> x | Error e -> failwith e
let content c = match Content.TryCreate c with Ok x -> x | Error e -> failwith e
let username u = match Username.TryCreate u with Ok x -> x | Error e -> failwith e

let docRoot id t c : Document.Root =
    { Id = DocumentId.OfGuid id; Title = title t; Content = content c }
