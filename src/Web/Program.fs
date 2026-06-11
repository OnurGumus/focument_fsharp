/// The delivery layer: a minimal ASP.NET app over the composition root. App.build
/// wires the whole write side; the endpoints below are one-liners over the typed
/// deps. The static UI in wwwroot is the same one the C# workshop serves.
module Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    let dbPath =
        match Environment.GetEnvironmentVariable "FOCUMENT_DB_PATH" with
        | null
        | "" -> "focument_fsharp.db"
        | p -> p

    let connString = $"Data Source={dbPath};"

    let app = builder.Build()
    let loggerFactory = app.Services.GetRequiredService<ILoggerFactory>()
    let endpoints = App.build app.Configuration loggerFactory connString

    app.UseDefaultFiles() |> ignore
    app.UseStaticFiles() |> ignore

    app.MapGet("/api/documents", Func<_>(fun () -> endpoints.GetDocuments())) |> ignore
    app.MapGet("/api/document/{id}/history", Func<HttpContext, _>(fun ctx -> endpoints.GetDocumentHistory ctx)) |> ignore
    app.MapPost("/api/document", Func<HttpContext, _>(fun ctx -> endpoints.CreateOrUpdate ctx |> Async.StartImmediateAsTask)) |> ignore
    app.MapPost("/api/document/restore", Func<HttpContext, _>(fun ctx -> endpoints.Restore ctx |> Async.StartImmediateAsTask)) |> ignore
    // Colleague approval of a held (over-quota) document.
    app.MapPost("/api/document/approve", Func<HttpContext, _>(fun ctx -> endpoints.Review(true, ctx) |> Async.StartImmediateAsTask)) |> ignore
    app.MapPost("/api/document/reject", Func<HttpContext, _>(fun ctx -> endpoints.Review(false, ctx) |> Async.StartImmediateAsTask)) |> ignore

    app.Run()
    0
