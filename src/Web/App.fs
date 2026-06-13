/// The composition root. Builds the actor system and wires the two aggregates,
/// the quota saga and the projection through the FCQRS F# facade pipeline:
/// register aggregates -> build the saga from their factories -> wire the
/// saga-starter -> register the projection -> hand the captured dependencies to a
/// fresh Endpoints instance.
module App

open FCQRS.Common
open FCQRS.FSharp
open Focument

let build config loggerFactory connString =
    // The read model lives in the same SQLite file as the Akka journal.
    Db.ensureTables connString

    let api =
        Fcqrs.actor config loggerFactory (Some(Fcqrs.connect FCQRS.Actor.DBType.Sqlite connString)) "FocumentCluster"

    // Aggregates first — registering them yields typed handles (factory + send).
    let documents =
        Fcqrs.aggregate api { Name = "Document"; Initial = Document.initial; Decide = Document.decide; Fold = Document.fold; Snapshots = SnapshotPolicy.Default }

    let users =
        Fcqrs.aggregate api { Name = "User"; Initial = User.initial; Decide = User.decide; Fold = User.fold; Snapshots = SnapshotPolicy.Default }

    // The saga is built from the two aggregates' factories (cross-reference
    // resolved by ordinary scope), then registered and wired into the starter.
    let quota =
        Fcqrs.saga api (QuotaSaga.definition documents.Factory users.Factory)

    Fcqrs.wireSagaStarters api [ quota ]

    // Projection last, resuming from the last committed offset. The filtered
    // handler returns Publish/Suppress per event (FCQRS.FSharp.Projection.filtered
    // qualified to dodge the local Projection module name).
    let subscriptions =
        Fcqrs.projection api (FCQRS.FSharp.Projection.filtered (int (Db.getLastOffset connString)) (Projection.handle loggerFactory connString))

    Endpoints(connString, subscriptions, documents)
