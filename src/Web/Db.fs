/// The SQLite read model: schema + read queries (Dapper). Same shape as the C#
/// ServerQuery / Projection.EnsureTables.
module Db

open System
open Microsoft.Data.Sqlite
open Dapper
open ReadModel

let private openConn connString =
    let c = new SqliteConnection(connString)
    c.Open()
    c

let ensureTables connString =
    use conn = openConn connString
    conn.Execute
        """
        CREATE TABLE IF NOT EXISTS Documents (
            Id TEXT PRIMARY KEY, Title TEXT NOT NULL, Body TEXT NOT NULL,
            Version INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
            ApprovalStatus TEXT NOT NULL DEFAULT 'Pending', Owner TEXT NOT NULL DEFAULT '')
        """
    
    |> ignore
    conn.Execute "CREATE TABLE IF NOT EXISTS Offsets (OffsetName TEXT PRIMARY KEY, OffsetCount INTEGER NOT NULL)"
    |> ignore
    conn.Execute "INSERT OR IGNORE INTO Offsets (OffsetName, OffsetCount) VALUES ('DocumentProjection', 0)"
    |> ignore
    conn.Execute
        """
        CREATE TABLE IF NOT EXISTS DocumentVersions (
            Id TEXT NOT NULL, Version INTEGER NOT NULL, Title TEXT NOT NULL, Body TEXT NOT NULL,
            CreatedAt TEXT NOT NULL, PRIMARY KEY (Id, Version))
        """
    
    |> ignore

let getLastOffset connString =
    use conn = openConn connString
    conn.ExecuteScalar("SELECT COALESCE(OffsetCount, 0) FROM Offsets WHERE OffsetName = 'DocumentProjection'")
    |> Convert.ToInt64

let private docColumns =
    "Id, Title, Body, Version, CreatedAt, UpdatedAt, ApprovalStatus, Owner"

let getDocuments connString =
    use conn = openConn connString
    conn.Query<DocumentDto> $"SELECT {docColumns} FROM Documents ORDER BY UpdatedAt DESC"
    |> Seq.toArray

let getDocument connString (docId: string) =
    use conn = openConn connString
    conn.Query<DocumentDto>($"SELECT {docColumns} FROM Documents WHERE Id = @Id", {| Id = docId |} :> obj)
    |> Seq.tryHead

let getDocumentHistory connString (docId: string) =
    use conn = openConn connString
    conn.Query<DocumentVersionDto>(
        "SELECT Id, Version, Title, Body, CreatedAt FROM DocumentVersions WHERE Id = @Id ORDER BY Version DESC",
        {| Id = docId |} :> obj
    )
    |> Seq.toArray
