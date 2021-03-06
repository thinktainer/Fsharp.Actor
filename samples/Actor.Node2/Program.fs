﻿// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.

open System
open FSharp.Actor

let fractureTransport = 
    new Fracture.FractureTransport(6666)

let logger = 
    Actor.spawn (Actor.Options.Create("node2/logger")) 
       (fun (actor:IActor<string>) ->
            let log = (actor :?> Actor.T<string>).Log
            let rec loop() = 
                async {
                    let! (msg, sender) = actor.Receive()
                    log.Debug(sprintf "%A sent %A" sender msg, None)
                    match sender with
                    | Some(s) -> 
                        s <-- "pong"
                    | None -> ()
                    return! loop()
                }
            loop()
        )

[<EntryPoint>]
let main argv = 
    Registry.Transport.register fractureTransport
    
    logger <-- "Hello"

    "node2/logger" ?<-- "Hello"
    Console.ReadLine() |> ignore

    while Console.ReadLine() <> "exit" do
        "actor.fracture://127.0.0.1:6667/node1/logger" ?<-- "Ping"

    Console.ReadLine() |> ignore

    0
