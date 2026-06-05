module Main

open Expecto

[<EntryPoint>]
let main argv =
    [ DocumentTests.tests; UserQuotaTests.tests; IntegrationTests.tests ]
    |> testList "focument"
    |> runTestsWithCLIArgs [] argv
