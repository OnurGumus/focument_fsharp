/// The write-side value objects, built on FCQRS's ValueLens contract: each type
/// exposes `Value_` (getter x validating setter) and everything else — the
/// TryCreate/Value members below, and the generic ValueLens.Value /
/// ValueLens.TryCreate / ValueLens.Create call styles — derives from it.
/// Representations are private: the validating constructor is the only door.
module Values

open System
open FCQRS.Model.Data

type DocumentId =
    private
    | DocumentId of Guid

    /// ValueLens contract: DocumentId <-> Guid (total — every Guid is valid).
    static member Value_ = (fun (DocumentId g) -> g), (fun (g: Guid) _ -> DocumentId g)

    static member Create() : DocumentId = ValueLens.Create(Guid.NewGuid())
    static member OfGuid(g: Guid) : DocumentId = ValueLens.Create g

    static member TryParse(s: string) =
        match Guid.TryParse s with
        | true, g -> Some(DocumentId.OfGuid g)
        | _ -> None

    member this.Value: Guid = ValueLens.Value this
    override this.ToString() = (ValueLens.Value this).ToString()

type Title =
    private
    | Title of ShortString

    /// ValueLens contract: Title <-> raw string, validated through ShortString.
    static member Value_ =
        (fun (Title s) -> (ValueLens.Value s: string)),
        (fun (s: string) _ -> ValueLens.TryCreate s |> Result.map Title |> Result.mapError (fun _ -> "Invalid title"))

    static member TryCreate(s: string) : Result<Title, string> = ValueLens.TryCreate s
    member this.Value: string = ValueLens.Value this
    override this.ToString() = this.Value

type Content =
    private
    | Content of LongString

    /// ValueLens contract: Content <-> raw string, validated through LongString.
    static member Value_ =
        (fun (Content s) -> (ValueLens.Value s: string)),
        (fun (s: string) _ -> ValueLens.TryCreate s |> Result.map Content |> Result.mapError (fun _ -> "Invalid content"))

    static member TryCreate(s: string) : Result<Content, string> = ValueLens.TryCreate s
    member this.Value: string = ValueLens.Value this
    override this.ToString() = this.Value

type Username =
    private
    | Username of ShortString

    /// ValueLens contract: Username <-> raw string, validated through ShortString.
    static member Value_ =
        (fun (Username s) -> (ValueLens.Value s: string)),
        (fun (s: string) _ -> ValueLens.TryCreate s |> Result.map Username |> Result.mapError (fun _ -> "a username is required"))

    static member TryCreate(s: string) : Result<Username, string> = ValueLens.TryCreate s
    member this.Value: string = ValueLens.Value this
    override this.ToString() = this.Value
