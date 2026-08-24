module MessagePrompts

let prompt (msg:string) =
    System.Console.Write(msg)
    System.Console.ReadLine().Trim()
    |> function | "" -> None | s -> Some s
    |> Option.map (fun s -> s.Replace ("\"","\\\""))

let private isCi () =
    match System.Environment.GetEnvironmentVariable "CI" with
    | null | "" -> false
    | value -> value.Equals("true", System.StringComparison.OrdinalIgnoreCase) || value = "1"

let rec promptYesNo msg =
    if isCi () then
        System.Console.WriteLine(sprintf "%s [Yn]: (CI env detected, auto-accepting)" msg)
        true
    else
        match prompt (sprintf "%s [Yn]: " msg) with
        | Some "Y" | Some "y" -> true
        | Some "N" | Some "n" -> false
        | _ -> System.Console.WriteLine("Sorry, invalid answer"); promptYesNo msg

let releaseMsg = """This will stage all uncommitted changes, push them to the origin and bump the release version to the latest number in the RELEASE_NOTES.md file. 
    Do you want to continue?"""
