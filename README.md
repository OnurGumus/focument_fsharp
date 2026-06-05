# focument (F#)

A pure-F# re-implementation of the **focument** CQRS / event-sourcing app — the
same domain as the C# workshop, but written natively in F# on top of a new
**idiomatic-F# functional facade** for [FCQRS](https://github.com/OnurGumus/FCQRS).

It's a document store where:

- creating a document is **quota-gated** — each user gets `Limit = 3` creations per
  rolling minute (a `User` aggregate enforces it);
- when you're **over quota**, the write is parked `AwaitingApproval` for a
  **colleague** (a different user) to `Approve` or `Reject`;
- **editing** an existing document (or **restoring** an old version) skips the quota;
- every write is **versioned** with full history.

The create → quota-check → approve/hold flow is coordinated by a **saga** that spans
the `Document` and `User` aggregates.

## How it's built

This app does **not** use FCQRS's C# OOP host-builder (`Aggregate<>`/`Saga<>` base
classes, `AddFcqrs`/`AddAggregate`…). Instead it uses the F# facade `FCQRS.FSharp`,
where everything is a **record of functions** wired with an explicit pipeline:

```fsharp
let api       = Fcqrs.actor config loggerFactory (Some (Fcqrs.connect DBType.Sqlite conn)) "FocumentCluster"
let documents = Fcqrs.aggregate api { Name = "Document"; Initial = Document.initial; Decide = Document.decide; Fold = Document.fold }
let users     = Fcqrs.aggregate api { Name = "User";     Initial = User.initial;     Decide = User.decide;     Fold = User.fold }
let quota     = Fcqrs.saga<QuotaSaga.Data, QuotaSaga.State, Document.Event> api (QuotaSaga.definition documents.Factory users.Factory)
Fcqrs.wireSagaStarters api [ quota ]
let subs      = Fcqrs.projection api { LastOffset = ...; Handle = Projection.handle ... }
```

- aggregates are `{ Name; Initial; Decide; Fold }` → a handle with a typed
  `.Send cid id command filter` and a `.Factory`;
- the saga is `{ Name; InitialData; Originator; HandleEvent; ApplySideEffects; StartOn }`,
  with `toOriginator` / `toAggregate` for its side-effect commands;
- the projection is `{ LastOffset; Handle }` → an `ISubscribe` for read-your-writes.

See `src/Web/App.fs` for the whole composition root.

## Layout

```
src/Domain/   Values, Document, User, QuotaSaga, ReadModel   (pure domain: DUs + decide/fold + saga)
src/Web/      Db, Projection, App, Endpoints, Program         (SQLite read model + F# Minimal-API host)
tests/Domain.Tests/   Document + User-quota (FakeTimeProvider) + an integration test (Expecto)
```

The web UI in `src/Web/wwwroot` is the same one the C# workshop serves.

## Run

```bash
dotnet run --project src/Web
# open http://localhost:5000  (set FOCUMENT_DB_PATH to choose the SQLite file)
```

Demo: as `alice`, create 3 documents (all "Document saved!"), then a 4th
("Quota exceeded — sent for approval."). As `bob`, approve it. `alice` can't approve
her own. Edit a document to bump its version; open history to restore an old version.

## Test

```bash
dotnet test tests/Domain.Tests        # 11 tests: pure decide/fold + quota window + an integration boot
```

## FCQRS reference

Both `FCQRS` `6.0.0-preview13` (which introduces the `FCQRS.FSharp` facade) and
`FCQRS.Model` are resolved from NuGet.org — no local feed or `nuget.config`
needed. The repo is self-contained: `dotnet build` restores everything.
