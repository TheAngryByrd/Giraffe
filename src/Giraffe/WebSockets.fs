module Giraffe.WebSocket

open System
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks
open Giraffe.Tasks
open Giraffe

type SocketStatus =
    | Connected of string


[<RequireQualifiedAccess>]
type WebSocketMsg<'Data> =
   | StatusMsg of SocketStatus
   | Opening
   | Closing
   | Error of string
   | Data of 'Data

type WebSocketSubprotocol = {
    Name : string
}

let DefaultWebSocketOptions = 
    let webSocketOptions = Microsoft.AspNetCore.Builder.WebSocketOptions()
    webSocketOptions.KeepAliveInterval <- TimeSpan.FromSeconds 120.
    webSocketOptions.ReceiveBufferSize <- 4 * 1024
    webSocketOptions

let private negotiateSubProtocol(requestedSubProtocols,supportedProtocols:seq<WebSocketSubprotocol>) =
    supportedProtocols
    |> Seq.tryFind (fun (supported:WebSocketSubprotocol) ->
        requestedSubProtocols |> Seq.contains supported.Name)

type WebSocketReference = {
    WebSocket : WebSocket
    Subprotocol : WebSocketSubprotocol option
    ID : string
}
    with
        member this.SendTextAsync(msg:string,?cancellationToken) = task {
            let byteResponse = System.Text.Encoding.UTF8.GetBytes msg
            let segment = ArraySegment<byte>(byteResponse, 0, byteResponse.Length)

            if not (isNull this.WebSocket) then
                if this.WebSocket.State = WebSocketState.Open then
                    let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
                    do! this.WebSocket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken)
        }
        
        member this.CloseAsync(?reason,?cancellationToken) = task {
            if not (isNull this.WebSocket) then
                if this.WebSocket.State = WebSocketState.Open then
                    let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
                    let reason = reason |> Option.defaultValue "Closed by the WebSocketManager"
                    do! this.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken)
        }

        static member FromWebSocket(websocket,?webSocketID,?subProtocol) : WebSocketReference = {
            WebSocket  = websocket
            Subprotocol = subProtocol
            ID = webSocketID |> Option.defaultWith (fun _ -> Guid.NewGuid().ToString())
        }

type private WebSocketConnectionDictionary() =
    let sockets = new System.Collections.Concurrent.ConcurrentDictionary<string, WebSocketReference>()

    with 
        member __.Add(reference:WebSocketReference) =
            sockets.AddOrUpdate(reference.ID, reference, Func<_,_,_>(fun _ _ -> reference)) |> ignore

        member __.Remove websocketID = 
            match sockets.TryRemove websocketID with
            | true, s -> Some s
            | _ -> None


        member __.TryGetReference websocketID = 
            match sockets.TryGetValue websocketID with
            | true, s -> Some s
            | _ -> None

        member __.AllConnections = sockets |> Seq.toArray

        member __.Count = sockets.Count

type ConnectionManager(?messageSize) =
    let messageSize = defaultArg messageSize DefaultWebSocketOptions.ReceiveBufferSize

    let connections = WebSocketConnectionDictionary()

    with

        member __.TryGetWebSocket(websocketID:string) : WebSocketReference option = 
            connections.TryGetReference websocketID

        member __.Count = connections.Count

        member __.BroadcastTextAsync(msg:string,cancellationToken:CancellationToken) = task {
            let byteResponse = System.Text.Encoding.UTF8.GetBytes msg
            let segment = ArraySegment<byte>(byteResponse, 0, byteResponse.Length)

            let! _ =
                connections.AllConnections
                |> Seq.map (fun kv -> task {
                    try
                        if not cancellationToken.IsCancellationRequested then
                            let webSocket = kv.Value.WebSocket
                            if webSocket.State = WebSocketState.Open then
                                do! webSocket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken)
                            else
                                if webSocket.State = WebSocketState.Closed then
                                    connections.Remove kv.Key |> ignore
                    with
                    | _ -> () })
                |> Task.WhenAll
            return ()
        }

        member private __.Receive<'Msg> (reference:WebSocketReference,messageF: WebSocketReference -> string -> Task<unit>,cancellationToken:CancellationToken) = task {
            let buffer = Array.zeroCreate messageSize |> ArraySegment<byte>
            use memoryStream = new IO.MemoryStream()
            let mutable endOfMessage = false
            let mutable keepRunning = Unchecked.defaultof<_>

            while not endOfMessage do
                let! received = reference.WebSocket.ReceiveAsync(buffer, cancellationToken)
                if received.CloseStatus.HasValue then
                    do! reference.WebSocket.CloseAsync(received.CloseStatus.Value, received.CloseStatusDescription, cancellationToken)
                    keepRunning <- false
                    endOfMessage <- true
                else
                    memoryStream.Write(buffer.Array,buffer.Offset,received.Count)
                    if received.EndOfMessage then
                        match received.MessageType with
                        | WebSocketMessageType.Binary ->
                            raise (NotImplementedException())
                        | WebSocketMessageType.Close ->
                            keepRunning <- false 
                            endOfMessage <- true
                        | WebSocketMessageType.Text ->
                            let! r = 
                                memoryStream.ToArray()
                                |> System.Text.Encoding.UTF8.GetString
                                |> fun s -> s.TrimEnd(char 0)
                                |> messageF reference

                            keepRunning <- true
                            endOfMessage <- true
                        | _ ->
                            raise (NotImplementedException())

            return keepRunning
        }

        member private this.RegisterClient<'Msg>(reference:WebSocketReference,connectedF: WebSocketReference -> Task<unit>,messageF,cancellationToken:CancellationToken) = task {
            connections.Add reference
            do! connectedF reference
            let mutable running = true
            while running && not cancellationToken.IsCancellationRequested do
                let! msg = this.Receive<'Msg>(reference,messageF,cancellationToken)
                running <- msg

            return! this.Disconnecting(reference.ID)
        }

        member private __.Disconnecting (websocketID:string) = task {
            match connections.Remove websocketID with
            | Some reference -> do! reference.CloseAsync()
            | _ -> ()
        }

        member this.CreateSocket(onConnected,onMessage,?webSocketID,?supportedProtocols:seq<WebSocketSubprotocol>,?cancellationToken) =
            fun next (ctx : Microsoft.AspNetCore.Http.HttpContext) -> task {
                let run(websocket) = task {
                    let webSocketID = webSocketID |> Option.defaultWith (fun _ -> Guid.NewGuid().ToString())
                    let reference = WebSocketReference.FromWebSocket(websocket,webSocketID=webSocketID)
                    let cancellationToken = cancellationToken |> Option.defaultValue CancellationToken.None
                    do! this.RegisterClient(reference,onConnected,onMessage,cancellationToken)
                    return! Successful.ok (text "OK") next ctx
                }

                if ctx.WebSockets.IsWebSocketRequest then
                    match supportedProtocols with
                    | None ->
                        let! (websocket : WebSocket) = ctx.WebSockets.AcceptWebSocketAsync()
                        return! run(websocket)
                    | Some supportedProtocols ->
                        match negotiateSubProtocol(ctx.WebSockets.WebSocketRequestedProtocols,supportedProtocols) with
                        | Some subProtocol ->
                            let! (websocket : WebSocket) = ctx.WebSockets.AcceptWebSocketAsync(subProtocol.Name)
                            return! run(websocket)
                        | None ->
                            return! HttpStatusCodeHandlers.RequestErrors.badRequest (text "websocket subprotocol not supported") next ctx
                else
                    return! HttpStatusCodeHandlers.RequestErrors.badRequest (text "no websocket request") next ctx
            }