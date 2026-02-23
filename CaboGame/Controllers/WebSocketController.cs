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
    public class WebSocketController : ControllerBase
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
            string playerId = null;
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
                        var type = root.GetProperty("type").GetString();
                        switch (type)
                        {
                            case "create_lobby":
                            {
                                playerId = Guid.NewGuid().ToString();
                                var playerName = root.GetProperty("playerName").GetString();
                                var lobbyId = Guid.NewGuid().ToString().Substring(0, 6);
                                var player = new Player { Id = playerId, Name = playerName };
                                var lobby = new Lobby { LobbyId = lobbyId };
                                lobby.Players.Add(player);
                                _lobbyManager.Lobbies[lobbyId] = lobby;
                                lock (_lock) _connections[playerId] = webSocket;
                                await SendLobbyUpdate(lobby);
                                break;
                            }
                            case "join_lobby":
                            {
                                playerId = Guid.NewGuid().ToString();
                                var joinLobbyId = root.GetProperty("lobbyId").GetString();
                                var joinPlayerName = root.GetProperty("playerName").GetString();
                                if (_lobbyManager.Lobbies.TryGetValue(joinLobbyId, out var joinLobby))
                                {
                                    if (joinLobby.GameStarted || joinLobby.Players.Count >= 4)
                                    {
                                        await SendError(webSocket, "Lobby full or already started");
                                        break;
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
                                break;
                            }
                            case "start_game":
                            {
                                var startLobbyId = root.GetProperty("lobbyId").GetString();
                                if (_lobbyManager.Lobbies.TryGetValue(startLobbyId, out var startLobby))
                                {
                                    if (startLobby.Players.Count < 2)
                                    {
                                        await SendError(webSocket, "Need at least 2 players");
                                        break;
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
                                    await SendError(webSocket, "Lobby not found");
                                }
                                break;
                            }
                            case "player_action":
                            {
                                var actionLobbyId = root.GetProperty("lobbyId").GetString();
                                var actionType = root.GetProperty("actionType").GetString();
                                var payload = root.TryGetProperty("payload", out var p) ? p : default;
                                if (!_gameManager.Games.TryGetValue(actionLobbyId, out var actionGame)) break;
                                var playerIdx = actionGame.Players.FindIndex(pp => pp.Id == playerId);
                                if (playerIdx == -1 || actionGame.GameOver) break;
                                var actingPlayer = actionGame.Players[playerIdx];

                                switch (actionType)
                                {
                                    case "peek_initial":
                                    {
                                        if (actingPlayer.InitialPeeksRemaining <= 0) { await SendError(webSocket, "No peeks remaining"); break; }
                                        int idx = payload.GetProperty("index").GetInt32();
                                        if (idx < 0 || idx >= 4) { await SendError(webSocket, "Invalid index"); break; }
                                        var card = actingPlayer.Hand[idx];
                                        actingPlayer.InitialPeeksRemaining -= 1;
                                        await SendJson(webSocket, new { type = "peek_result", index = idx, card });
                                        break;
                                    }
                                    case "draw":
                                    {
                                        if (actionGame.Phase != "draw" || actionGame.CurrentTurn != playerIdx) break;
                                        if (actionGame.Deck.Count == 0) { await SendError(webSocket, "Deck is empty"); break; }
                                        var card = actionGame.Deck[0];
                                        actionGame.Deck.RemoveAt(0);
                                        actionGame.PendingCard = card;
                                        actionGame.PendingPlayerId = actingPlayer.Id;
                                        if (_connections.TryGetValue(actingPlayer.Id, out var ws) && ws.State == WebSocketState.Open)
                                        {
                                            await SendJson(ws, new { type = "draw_offer", card });
                                        }
                                        break;
                                    }
                                    case "resolve_draw":
                                    {
                                        if (actionGame.PendingPlayerId != actingPlayer.Id) break;
                                        var action = payload.GetProperty("action").GetString();
                                        var drawn = actionGame.PendingCard;
                                        actionGame.PendingCard = null;
                                        actionGame.PendingPlayerId = null;
                                        if (action == "discard")
                                        {
                                            actionGame.DiscardPile.Add(drawn);
                                            actionGame.LastAction = new { type = "discard", player = actingPlayer.Id, card = drawn };
                                            actionGame.Phase = "draw";
                                            actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                        }
                                        else if (action == "swap")
                                        {
                                            int cardIdx = payload.GetProperty("cardIndex").GetInt32();
                                            var replaced = actingPlayer.Hand[cardIdx];
                                            actingPlayer.Hand[cardIdx] = drawn;
                                            actionGame.DiscardPile.Add(replaced);
                                            actionGame.LastAction = new { type = "swap", player = actingPlayer.Id, card = drawn };
                                            actionGame.Phase = "draw";
                                            actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                        }
                                        else if (action == "use_ability")
                                        {
                                            // handle abilities: peek (7-8), spy (9-10), swap (11-12)
                                            var targetPlayerId = payload.GetProperty("targetPlayer").GetString();
                                            var targetIdx = payload.GetProperty("targetIdx").GetInt32();
                                            var cardVal = int.TryParse(drawn, out var cv) ? cv : -1;
                                            if (cardVal >= 7 && cardVal <= 8)
                                            {
                                                // peek: send target card value privately
                                                var tp = actionGame.Players.FirstOrDefault(x => x.Id == targetPlayerId);
                                                if (tp != null)
                                                {
                                                    var revealedCard = tp.Hand[targetIdx];
                                                    if (_connections.TryGetValue(actingPlayer.Id, out var aws) && aws.State == WebSocketState.Open)
                                                    {
                                                        await SendJson(aws, new { type = "ability_peek", targetPlayer = targetPlayerId, index = targetIdx, card = revealedCard });
                                                    }
                                                }
                                            }
                                            else if (cardVal >= 9 && cardVal <= 10)
                                            {
                                                // spy: like peek (synonym)
                                                var tp = actionGame.Players.FirstOrDefault(x => x.Id == targetPlayerId);
                                                if (tp != null)
                                                {
                                                    var revealedCard = tp.Hand[targetIdx];
                                                    if (_connections.TryGetValue(actingPlayer.Id, out var aws) && aws.State == WebSocketState.Open)
                                                    {
                                                        await SendJson(aws, new { type = "ability_spy", targetPlayer = targetPlayerId, index = targetIdx, card = revealedCard });
                                                    }
                                                }
                                            }
                                            else if (cardVal >= 11 && cardVal <= 12)
                                            {
                                                // swap ability: swap acting player's chosen index with target player's chosen index
                                                var tp = actionGame.Players.FirstOrDefault(x => x.Id == targetPlayerId);
                                                if (tp != null)
                                                {
                                                    int actorIdx = payload.GetProperty("actorIdx").GetInt32();
                                                    var tmp = actingPlayer.Hand[actorIdx];
                                                    actingPlayer.Hand[actorIdx] = tp.Hand[targetIdx];
                                                    tp.Hand[targetIdx] = tmp;
                                                }
                                            }

                                            // after ability, place drawn card to discard
                                            actionGame.DiscardPile.Add(drawn);
                                            actionGame.LastAction = new { type = "ability_used", player = actingPlayer.Id, card = drawn };
                                            actionGame.Phase = "draw";
                                            actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                        }
                                        await SendGameUpdate(actionGame, null);
                                        break;
                                    }
                                    case "take_discard":
                                    {
                                        if (actionGame.DiscardPile.Count == 0) { await SendError(webSocket, "Discard empty"); break; }
                                        int cardIdx = payload.GetProperty("cardIndex").GetInt32();
                                        var top = actionGame.DiscardPile.Last();
                                        actionGame.DiscardPile.RemoveAt(actionGame.DiscardPile.Count - 1);
                                        var replaced = actingPlayer.Hand[cardIdx];
                                        actingPlayer.Hand[cardIdx] = top;
                                        actionGame.DiscardPile.Add(replaced);
                                        actionGame.LastAction = new { type = "take_discard", player = actingPlayer.Id, card = top };
                                        actionGame.Phase = "draw";
                                        actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                        await SendGameUpdate(actionGame, null);
                                        break;
                                    }
                                    case "call_cabo":
                                    {
                                        actionGame.GameOver = true;
                                        actionGame.Winner = actingPlayer.Id;
                                        await SendGameUpdate(actionGame, "called Cabo!");
                                        break;
                                    }
                                }
                                break;
                            }
                            case "chat_message":
                            {
                                var chatLobbyId = root.GetProperty("lobbyId").GetString();
                                var chatMsg = root.GetProperty("message").GetString();
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
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        await SendError(webSocket, "Invalid message: " + ex.Message);
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
                        selfPlayerId = player.Id
                    };
                    await SendJson(ws, msg);
                }
            }
        }

        private async Task SendGameStart(GameState gameState)
        {
            var playersDto = gameState.Players.Select(p => new { id = p.Id, name = p.Name, hand = p.Hand, revealed = p.Revealed, initialPeeksRemaining = p.InitialPeeksRemaining }).ToList();
            foreach (var player in gameState.Players)
            {
                if (_connections.TryGetValue(player.Id, out var ws) && ws.State == WebSocketState.Open)
                {
                    var dto = new
                    {
                        type = "game_start",
                        lobbyId = gameState.LobbyId,
                        gameState = new
                        {
                            lobbyId = gameState.LobbyId,
                            players = playersDto,
                            deck = gameState.Deck,
                            discardPile = gameState.DiscardPile,
                            currentTurn = gameState.CurrentTurn,
                            phase = gameState.Phase,
                            gameOver = gameState.GameOver,
                            winner = gameState.Winner
                        },
                        selfPlayerId = player.Id
                    };
                    await SendJson(ws, dto);
                }
            }
        }

        private async Task SendGameUpdate(GameState gameState, string result)
        {
            var playersDto = gameState.Players.Select(p => new { id = p.Id, name = p.Name, hand = p.Hand, revealed = p.Revealed, initialPeeksRemaining = p.InitialPeeksRemaining }).ToList();
            foreach (var player in gameState.Players)
            {
                if (_connections.TryGetValue(player.Id, out var ws) && ws.State == WebSocketState.Open)
                {
                    var dto = new
                    {
                        type = "game_update",
                        lobbyId = gameState.LobbyId,
                        gameState = new
                        {
                            lobbyId = gameState.LobbyId,
                            players = playersDto,
                            deck = gameState.Deck,
                            discardPile = gameState.DiscardPile,
                            currentTurn = gameState.CurrentTurn,
                            phase = gameState.Phase,
                            gameOver = gameState.GameOver,
                            winner = gameState.Winner
                        },
                        result,
                        selfPlayerId = player.Id
                    };
                    await SendJson(ws, dto);
                }
            }
        }

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

        private List<string> GenerateDeck()
        {
            var deck = new List<string>();
            // 2x 0
            deck.AddRange(Enumerable.Repeat("0", 2));
            // 4x 1-12
            for (int v = 1; v <= 12; v++)
            {
                deck.AddRange(Enumerable.Repeat(v.ToString(), 4));
            }
            // 2x 13
            deck.AddRange(Enumerable.Repeat("13", 2));
            // shuffle
            var rng = new Random();
            return deck.OrderBy(_ => rng.Next()).ToList();
        }

        private void DealCards(GameState gameState)
        {
            foreach (var player in gameState.Players)
            {
                player.InitialPeeksRemaining = 2;
                for (int i = 0; i < 4; i++)
                {
                    player.Hand[i] = gameState.Deck[0];
                    gameState.Deck.RemoveAt(0);
                    player.Revealed[i] = false;
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
