module TestPaths

open System
open System.IO

let findFile name =
    let starts =
        [ Directory.GetCurrentDirectory()
          AppContext.BaseDirectory
          Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..") ]

    starts
    |> Seq.collect (fun root ->
        [ Path.Combine(root, name)
          Path.Combine(root, "..", name)
          Path.Combine(root, "fsharp", name) ])
    |> Seq.map Path.GetFullPath
    |> Seq.tryFind File.Exists
    |> Option.defaultWith (fun () -> failwithf "could not find %s" name)
