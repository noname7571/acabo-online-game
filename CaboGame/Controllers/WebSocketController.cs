using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using CaboGame.Game;
using CaboGame.Game.Models;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CaboGame.Controllers
{
    [ApiController]
    [Route("ws")]
    public partial class WebSocketController : ControllerBase
    {
        private readonly LobbyManager _lobbyManager;
        private readonly GameManager _gameManager;
        private static readonly Dictionary<string, WebSocket> _connections = new();
        private static readonly object _lock = new();

        public WebSocketController(LobbyManager lobbyManager, GameManager gameManager)
        {
            _lobbyManager = lobbyManager;
            _gameManager = gameManager;
        }

        [HttpGet]
        public async Task Get()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocket(webSocket);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
            }
        }

        private async Task HandleWebSocket(WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            string? playerId = null;
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (playerId != null)
                    {
                        lock (_lock) _connections.Remove(playerId);
                    }
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                else
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    try
                    {
                        using var doc = JsonDocument.Parse(message);
                        var root = doc.RootElement;
                        var type = root.GetProperty("type").GetString() ?? string.Empty;
                        switch (type)
                        {
                            case "create_lobby":
                                playerId = await HandleCreateLobby(root, webSocket);
                                break;
                            case "join_lobby":
                                playerId = await HandleJoinLobby(root, webSocket);
                                break;
                            case "start_game":
                                await HandleStartGame(root);
                                break;
                            case "player_action":
                                await HandlePlayerAction(root, playerId, webSocket);
                                break;
                            case "chat_message":
                                await HandleChatMessage(root);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        await SendError(webSocket, "Invalid message: " + ex.Message);
                    }
                }
            }
        }


        // helper previously used for masking; no longer needed
        // (we send full hand to clients and let them decide visibility)
        // private List<object> BuildPlayersDto(...) { ... }

        private async Task SendError(WebSocket ws, string error)
        {
            var msg = new { type = "error", message = error };
            await SendJson(ws, msg);
        }

        private async Task SendJson(WebSocket ws, object obj)
        {
            var json = JsonSerializer.Serialize(obj);
            var buffer = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
