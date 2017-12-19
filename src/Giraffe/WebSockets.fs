module Giraffe.WebSocket

open System
open System.Net.WebSockets
open System.Threading
open Giraffe.Tasks

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

let NegotiateSubProtocol(requestedSubProtocols,supportedProtocols) =
    supportedProtocols
    |> Seq.tryFind (fun (supported:WebSocketSubprotocol) ->
        requestedSubProtocols |> Seq.contains supported.Name)

type private WebSocketConnectionDictionary() =
    let sockets = new System.Collections.Concurrent.ConcurrentDictionary<string, WebSocket>()

    with 
        member __.AddSocket(id:string,socket) =
            sockets.AddOrUpdate(id, socket, Func<_,_,_>(fun _ _ -> socket)) |> ignore

        member __.RemoveSocket websocketID = 
            match sockets.TryRemove websocketID with
            | true, s -> Some s
            | _ -> None


        member __.GetSocket websocketID = 
            match sockets.TryGetValue websocketID with
            | true, s -> Some s
            | _ -> None                

        member __.AllConnections = sockets |> Seq.toArray

type ConnectionManager(?messageSize) =
    let messageSize = defaultArg messageSize (4 * 1024)
    let connections = WebSocketConnectionDictionary()
    with
        member __.Send(webSocket:WebSocket,msg:string,cancellationToken) = task {
            let byteResponse =
                msg
                |> System.Text.Encoding.UTF8.GetBytes

            if not (isNull webSocket) then
                if webSocket.State = WebSocketState.Open then
                    do! webSocket.SendAsync(new ArraySegment<byte>(byteResponse, 0, byteResponse.Length), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false)
        }

        member __.GetSocket(id:string) = connections.GetSocket id

        member __.SendToAll(msg:string,cancellationToken:CancellationToken) = task {
            let byteResponse =
                msg
                |> System.Text.Encoding.UTF8.GetBytes

            for kv in connections.AllConnections do
                if not cancellationToken.IsCancellationRequested then
                    let webSocket = kv.Value
                    if webSocket.State = WebSocketState.Open then
                        do! webSocket.SendAsync(new ArraySegment<byte>(byteResponse, 0, byteResponse.Length), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false)
                    else
                        if webSocket.State = WebSocketState.Closed then
                            connections.RemoveSocket kv.Key |> ignore
        }

        member private __.Receive<'Msg> (webSocket:WebSocket,messageF,cancellationToken:CancellationToken) = task {
            let buffer = Array.zeroCreate messageSize |> ArraySegment<byte>
            use memoryStream = new IO.MemoryStream()

            // TODO: recursive with task aren't tail called optimized if i recall, may want to make iterative
            let rec receive' () = task {
                let! result = webSocket.ReceiveAsync(buffer, cancellationToken)
                if result.CloseStatus.HasValue then
                    do! webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, cancellationToken)
                    return WebSocketMsg.Closing
                else
                    memoryStream.Write(buffer.Array,buffer.Offset,result.Count)
                    if result.EndOfMessage then
                        match result.MessageType with
                        | WebSocketMessageType.Binary ->
                            return! raise (NotImplementedException())
                        | WebSocketMessageType.Close ->
                            return WebSocketMsg.Closing
                        | WebSocketMessageType.Text ->
                            let t : System.Threading.Tasks.Task<'Msg> = 
                                memoryStream.ToArray()
                                |> System.Text.Encoding.UTF8.GetString
                                |> fun s -> s.TrimEnd(char 0)
                                |> messageF
                            t.Wait()       

                            return WebSocketMsg.Data t.Result
                        | _ ->
                            return! raise (NotImplementedException())
                    else    
                        return! receive' ()
            }
            return! receive' ()
            
        }

        member this.RegisterClient<'Msg>(webSocket:WebSocket,websocketID,connectedF,messageF,cancellationToken:CancellationToken) = task {
            let mutable running = true
            connections.AddSocket(websocketID,webSocket)
            let t : System.Threading.Tasks.Task<unit> = connectedF webSocket websocketID
            t.Wait()
            while running && not cancellationToken.IsCancellationRequested do
                let! msg = this.Receive<'Msg>(webSocket,messageF,cancellationToken)
                match msg with  
                | WebSocketMsg.Closing ->
                    running <- false
                | _ -> ()
            return! this.Disconnecting(websocketID)
        }

        member this.RegisterClient<'Msg>(webSocket:WebSocket,connectedF,messageF,cancellationToken:CancellationToken) =
            let id = Guid.NewGuid().ToString()
            this.RegisterClient<'Msg>(webSocket,id,connectedF,messageF,cancellationToken)

        member private __.Disconnecting (websocketID:string) = task {
            match connections.RemoveSocket(websocketID) with
            | Some socket ->
                if not (isNull socket) then
                    if socket.State = WebSocketState.Open then
                        do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by the WebSocketManager", CancellationToken.None).ConfigureAwait(false)
            | _ -> ()
        }
