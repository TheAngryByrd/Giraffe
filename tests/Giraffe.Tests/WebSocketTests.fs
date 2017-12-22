module Giraffe.WebSocketTests 

open System
open System.Net.WebSockets
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http.Internal
open Microsoft.Extensions.Primitives
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Xunit
open Giraffe.Tasks
open Giraffe.HttpStatusCodeHandlers
open Giraffe.WebSocket
open System.Threading.Tasks
open System.Threading

[<Fact>]
let ``Echo Tests`` () =


    let useF (middlware : HttpContext -> (unit -> Task) -> Task<unit>) (app:IApplicationBuilder) =
        app.Use(
            Func<HttpContext,Func<Task>,Task>(
                fun ctx next -> 
                    middlware ctx next.Invoke  :> Task
                ))

    let echoSocket (connectionManager : ConnectionManager) (httpContext : HttpContext) (next : unit -> Task) = task {
        // ECHO
        if httpContext.WebSockets.IsWebSocketRequest then

            let! (websocket : WebSocket) = httpContext.WebSockets.AcceptWebSocketAsync()
            let connected socket id = task { 
                ()
            }

            let onMessage data = task {
                let! _ = SendText websocket data CancellationToken.None
                return WebSocketMsg.Data data
            }
            do! connectionManager.RegisterClient(websocket,connected,onMessage,CancellationToken.None)
            



            ()
        else
            do! next()
    }
    

    let cm = ConnectionManager()
    let configure (app : IApplicationBuilder) =
        app.UseWebSockets()
        |> useF (echoSocket cm)
        |> ignore
        let abc = Giraffe.HttpStatusCodeHandlers.Successful.ok (text "ok")
        app.UseGiraffe abc
    use server =
         new TestServer(
                WebHostBuilder()
                    .Configure(fun app -> configure app))
    let wsClient = server.CreateWebSocketClient()

    let websocket = wsClient.ConnectAsync(server.BaseAddress, System.Threading.CancellationToken.None).Result

    let expected = "Hello"
    let _ = (SendText websocket expected CancellationToken.None).Result
    let buffer = Array.zeroCreate 4096 |> ArraySegment<byte>
    
    let result = websocket.ReceiveAsync(buffer,  CancellationToken.None).Result

    let actual =
        buffer.Array
        |> System.Text.Encoding.UTF8.GetString
        |> fun s -> s.TrimEnd(char 0)

    Assert.Equal(expected,actual)
    ()
    