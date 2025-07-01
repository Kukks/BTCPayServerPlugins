using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.FileSeller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BitcoinSwitch;

[AllowAnonymous]
public class BitcoinSwitchController:ControllerBase
{
    private readonly BitcoinSwitchService _service;

    public BitcoinSwitchController(BitcoinSwitchService service)
    {
        _service = service;
    }

    [Route("~/apps/{id}/pos/bitcoinswitch")]
    public async Task Connect(string id)
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
           
            await Echo(id, webSocket);
        }
        else
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    
    private async Task Echo(string id, WebSocket webSocket)
    {
        try
        {
            _service.AppToSockets.Add(id, webSocket);
            var buffer = new byte[1024 * 4];
            var receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), CancellationToken.None);

            while (!receiveResult.CloseStatus.HasValue && webSocket.State == WebSocketState.Open)
            {

                receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), CancellationToken.None);
            }

            await webSocket.CloseAsync(
                receiveResult.CloseStatus.Value,
                receiveResult.CloseStatusDescription,
                CancellationToken.None);

        }
        finally
        {
            _service.AppToSockets.Remove(id, webSocket);
        }
    }
    
    
    
}