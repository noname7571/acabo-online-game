using System.Net.WebSockets;
using System.Text.Json;
using CaboGame.Game;
using CaboGame.Game.Models;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CaboGame.Controllers
{
    public partial class WebSocketController
    {
        // lobby-related message handling extracted from the original controller

        private async Task<string> HandleCreateLobby(JsonElement root, WebSocket webSocket)
        {
            var playerId = Guid.NewGuid().ToString();
            var playerName = root.GetProperty("playerName").GetString() ?? string.Empty;
            var lobbyId = Guid.NewGuid().ToString().Substring(0, 6);
            var player = new Player { Id = playerId, Name = playerName };
            var lobby = new Lobby { LobbyId = lobbyId };
            // read optional timer settings
            if (root.TryGetProperty("timerEnabled", out var te)) lobby.TimerEnabled = te.GetBoolean();
            if (root.TryGetProperty("turnSeconds", out var ts)) lobby.TurnSeconds = ts.GetInt32();
            lobby.Players.Add(player);
            _lobbyManager.Lobbies[lobbyId] = lobby;
            lock (_lock) _connections[playerId] = webSocket;
            await SendLobbyUpdate(lobby);
            return playerId;
        }

        private async Task<string> HandleJoinLobby(JsonElement root, WebSocket webSocket)
        {
            var playerId = Guid.NewGuid().ToString();
            var joinLobbyId = root.GetProperty("lobbyId").GetString() ?? string.Empty;
            var joinPlayerName = root.GetProperty("playerName").GetString() ?? string.Empty;
            if (_lobbyManager.Lobbies.TryGetValue(joinLobbyId, out var joinLobby))
            {
                if (joinLobby.GameStarted || joinLobby.Players.Count >= 4)
                {
                    await SendError(webSocket, "Lobby full or already started");
                    return playerId;
                }
                var joinPlayer = new Player { Id = playerId, Name = joinPlayerName };
                joinLobby.Players.Add(joinPlayer);
                lock (_lock) _connections[playerId] = webSocket;
                await SendLobbyUpdate(joinLobby);
            }
            else
            {
                await SendError(webSocket, "Lobby not found");
            }
            return playerId;
        }

        private async Task HandleStartGame(JsonElement root)
        {
            var startLobbyId = root.GetProperty("lobbyId").GetString() ?? string.Empty;
            if (_lobbyManager.Lobbies.TryGetValue(startLobbyId, out var startLobby))
            {
                if (startLobby.Players.Count < 2)
                {
                    await SendError(_connections.Values.FirstOrDefault() ?? null!, "Need at least 2 players");
                    return;
                }
                startLobby.GameStarted = true;
                var gameState = new GameState
                {
                    LobbyId = startLobbyId,
                    Players = startLobby.Players,
                    Deck = GenerateDeck(),
                    DiscardPile = new List<string>(),
                    CurrentTurn = 0,
                    Phase = "draw",
                    GameOver = false
                };
                DealCards(gameState);
                _gameManager.Games[startLobbyId] = gameState;
                await SendGameStart(gameState);
            }
            else
            {
                // no websocket available to send error here; SendError requires one. that case never happens in normal flow.
            }
        }

        private async Task HandleChatMessage(JsonElement root)
        {
            var chatLobbyId = root.GetProperty("lobbyId").GetString() ?? string.Empty;
            var chatMsg = root.GetProperty("message").GetString() ?? string.Empty;
            if (_lobbyManager.Lobbies.TryGetValue(chatLobbyId, out var chatLobby))
            {
                foreach (var p in chatLobby.Players)
                {
                    if (_connections.TryGetValue(p.Id, out var ws) && ws.State == WebSocketState.Open)
                    {
                        await SendJson(ws, new { type = "chat_message", lobbyId = chatLobbyId, playerName = p.Name, message = chatMsg });
                    }
                }
            }
        }

        private async Task SendLobbyUpdate(Lobby lobby)
        {
            var playersList = lobby.Players.Select(p => new { id = p.Id, name = p.Name }).ToList();
            foreach (var player in lobby.Players)
            {
                if (_connections.TryGetValue(player.Id, out var ws) && ws.State == WebSocketState.Open)
                {
                    var msg = new
                    {
                        type = "lobby_update",
                        lobbyId = lobby.LobbyId,
                        players = playersList,
                        timerEnabled = lobby.TimerEnabled,
                        turnSeconds = lobby.TurnSeconds,
                        selfPlayerId = player.Id
                    };
                    await SendJson(ws, msg);
                }
            }
        }

        // the DealCards helper is used when a game is started; it belongs alongside lobby logic
        private void DealCards(GameState gameState)
        {
            foreach (var player in gameState.Players)
            {
                player.InitialPeeksRemaining = 2;
                for (int i = 0; i < 4; i++)
                {
                    player.Hand.Add(gameState.Deck[0]);
                    gameState.Deck.RemoveAt(0);
                    player.Revealed.Add(false);
                }
            }
            // put one card to discard pile visible
            if (gameState.Deck.Count > 0)
            {
                var top = gameState.Deck[0];
                gameState.Deck.RemoveAt(0);
                gameState.DiscardPile.Add(top);
            }
        }
    }
}
