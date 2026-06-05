/// The write-side value objects. Title/Content/Username wrap FCQRS's validated
/// ShortString/LongString (created via ValueLens, returning a Result). The
/// constructors/extractors live on the types themselves as members rather than in
/// companion modules.
module Values
open System
open FCQRS.Model.Data

type DocumentId =
    | DocumentId of Guid

    static member Create() = DocumentId(Guid.NewGuid())
    static member OfGuid g= DocumentId g

    static member TryParse(s: string) =
        match Guid.TryParse s with
        | true, g -> Some(DocumentId g)
        | _ -> None

    member this.Value = let (DocumentId g) = this in g
    override this.ToString() = let (DocumentId g) = this in g.ToString()

type Title =
    | Title of ShortString

    static member TryCreate s =
        match ValueLens.TryCreate s with
        | Ok ss -> Ok(Title ss)
        | Error _ -> Error "Invalid title"

    member this.Value = let (Title s) = this in ValueLens.Value s

type Content =
    | Content of LongString

    static member TryCreate s=
        match ValueLens.TryCreate s with
        | Ok ss -> Ok(Content ss)
        | Error _ -> Error "Invalid content"

    member this.Value = let (Content s) = this in ValueLens.Value s

type Username =
    | Username of ShortString

    static member TryCreate s =
        match ValueLens.TryCreate s with
        | Ok ss -> Ok(Username ss)
        | Error _ -> Error "a username is required"

    member this.Value = let (Username s) = this in ValueLens.Value s
