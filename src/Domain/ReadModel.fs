/// Read-model DTOs the web returns as JSON. [<CLIMutable>] so Dapper can hydrate
/// them from the SQLite read tables.
module ReadModel

[<CLIMutable>]
type DocumentDto =
    { Id: string
      Title: string
      Body: string
      Version: int64
      CreatedAt: string
      UpdatedAt: string
      ApprovalStatus: string
      Owner: string }

[<CLIMutable>]
type DocumentVersionDto =
    { Id: string
      Version: int64
      Title: string
      Body: string
      CreatedAt: string }
