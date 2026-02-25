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
                            {
                                playerId = Guid.NewGuid().ToString();
                                    var playerName = root.GetProperty("playerName").GetString() ?? string.Empty;
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
                                    var joinLobbyId = root.GetProperty("lobbyId").GetString() ?? string.Empty;
                                    var joinPlayerName = root.GetProperty("playerName").GetString() ?? string.Empty;
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
                                  var startLobbyId = root.GetProperty("lobbyId").GetString() ?? string.Empty;
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
                                  var actionLobbyId = root.GetProperty("lobbyId").GetString() ?? string.Empty;
                                  var actionType = root.GetProperty("actionType").GetString() ?? string.Empty;
                                var payload = root.TryGetProperty("payload", out var p) ? p : default;
                                if (!_gameManager.Games.TryGetValue(actionLobbyId, out var actionGame)) break;
                                var playerIdx = actionGame.Players.FindIndex(pp => pp.Id == playerId);
                                if (playerIdx == -1 || actionGame.GameOver) break;
                                var actingPlayer = actionGame.Players[playerIdx];

                                switch (actionType)
                                {
                                    case "skip_turn":
                                    {
                                        // only skip on your turn
                                        if (actionGame.CurrentTurn == playerIdx && actionGame.Phase == "draw")
                                        {
                                            actionGame.PendingCard = null;
                                            actionGame.PendingPlayerId = null;
                                            actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                            await SendGameUpdate(actionGame, "turn skipped");
                                        }
                                        break;
                                    }
                                    case "peek_initial":
                                    {
                                        if (actingPlayer.InitialPeeksRemaining <= 0) { await SendError(webSocket, "No peeks remaining"); break; }
                                        int idx = payload.GetProperty("index").GetInt32();
                                        if (idx < 0 || idx >= actingPlayer.Hand.Count) { await SendError(webSocket, "Invalid index"); break; }
                                        var card = actingPlayer.Hand[idx];
                                        actingPlayer.InitialPeeksRemaining -= 1;
                                        await SendJson(webSocket, new { type = "peek_result", index = idx, card });
                                        // broadcast updated game state so clients update peek counters and deck/pile view
                                        await SendGameUpdate(actionGame, null);
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
                                        // broadcast deck change so clients see updated deck count and pending player
                                        await SendGameUpdate(actionGame, null);
                                        break;
                                    }
                                    case "resolve_draw":
                                    {
                                        if (actionGame.PendingPlayerId != actingPlayer.Id) break;
                                        var action = payload.GetProperty("action").GetString() ?? string.Empty;
                                        string? drawn = actionGame.PendingCard;
                                        actionGame.PendingCard = null;
                                        actionGame.PendingPlayerId = null;
                                        if (action == "discard")
                                        {
                                            actionGame.DiscardPile.Add(drawn ?? string.Empty);
                                            actionGame.LastAction = new { type = "discard", player = actingPlayer.Id, card = drawn ?? string.Empty };
                                            actionGame.Phase = "draw";
                                            actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                        }
                                        else if (action == "swap")
                                        {
                                            int cardIdx = payload.GetProperty("cardIndex").GetInt32();
                                            var replaced = actingPlayer.Hand[cardIdx];
                                            actingPlayer.Hand[cardIdx] = drawn ?? string.Empty;
                                            // do not discard replaced yet; make it the new pending card so player can choose again
                                            actionGame.PendingCard = replaced;
                                            actionGame.PendingPlayerId = actingPlayer.Id;
                                            actionGame.LastAction = new { type = "swap", player = actingPlayer.Id, card = drawn ?? string.Empty };
                                            // replay draw offer with the replaced card
                                            if (_connections.TryGetValue(actingPlayer.Id, out var replayWs) && replayWs.State == WebSocketState.Open)
                                            {
                                                await SendJson(replayWs, new { type = "draw_offer", card = replaced });
                                            }
                                            // keep phase "draw" and current turn unchanged
                                        }
                                        else if (action == "pair_claim")
                                        {
                                            // player attempts to put out 2-4 equal-value cards from their hand
                                            if (!payload.TryGetProperty("indices", out var idxArr) || idxArr.GetArrayLength() < 2)
                                            {
                                                await SendError(webSocket, "Invalid pair claim payload");
                                                break;
                                            }
                                            var indices = new List<int>();
                                            for (int ii = 0; ii < idxArr.GetArrayLength(); ii++)
                                                indices.Add(idxArr[ii].GetInt32());
                                            if (indices.Any(i => i < 0 || i >= actingPlayer.Hand.Count))
                                            {
                                                await SendError(webSocket, "Invalid indices");
                                                break;
                                            }
                                            // ensure all selected cards have same value
                                            var firstVal = actingPlayer.Hand[indices[0]];
                                            if (indices.All(i => actingPlayer.Hand[i] == firstVal))
                                            {
                                                // remove cards in descending order
                                                foreach (var i in indices.OrderByDescending(x => x))
                                                {
                                                    actingPlayer.Hand.RemoveAt(i);
                                                    actingPlayer.Revealed.RemoveAt(i);
                                                }
                                                foreach (var _ in indices) actionGame.DiscardPile.Add(firstVal ?? string.Empty);
                                                // add drawn to hand
                                                actingPlayer.Hand.Add(drawn ?? string.Empty);
                                                actingPlayer.Revealed.Add(false);
                                                actionGame.LastAction = new { type = "pair_claim_success", player = actingPlayer.Id, card = drawn ?? string.Empty, indices };
                                                actionGame.Phase = "draw";
                                                actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                            }
                                            else
                                            {
                                                // failure: just take drawn
                                                actingPlayer.Hand.Add(drawn ?? string.Empty);
                                                actingPlayer.Revealed.Add(false);
                                                actionGame.LastAction = new { type = "pair_claim_fail", player = actingPlayer.Id, card = drawn ?? string.Empty, indices };
                                                actionGame.Phase = "draw";
                                                actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                            }
                                        }
                                        else if (action == "use_ability")
                                        {
                                            // handle abilities: peek (7-8), spy (9-10), swap (11-12)
                                            var targetPlayerId = payload.GetProperty("targetPlayer").GetString() ?? string.Empty;
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
                                            actionGame.DiscardPile.Add(drawn ?? string.Empty);
                                            actionGame.LastAction = new { type = "ability_used", player = actingPlayer.Id, card = drawn ?? string.Empty };
                                            actionGame.Phase = "draw";
                                            actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                            // if cabo is pending, decrement countdown and finish if zero
                                            if (actionGame.CaboPending)
                                            {
                                                actionGame.CaboCountdown -= 1;
                                                if (actionGame.CaboCountdown <= 0)
                                                {
                                                    // final evaluation
                                                    actionGame.GameOver = true;
                                                    // compute simple totals
                                                    var totals = actionGame.Players.Select(pl => new { id = pl.Id, total = pl.Hand.Sum(h => int.TryParse(h, out var v) ? v : 0) }).ToList();
                                                    var winner = totals.OrderBy(t => t.total).First();
                                                    actionGame.Winner = winner.id;
                                                    await SendGameUpdate(actionGame, "Final scoring: " + string.Join(", ", totals.Select(t => t.id + ":" + t.total)));
                                                    break;
                                                }
                                            }
                                        }
                                        // if cabo is pending, decrement countdown for this completed turn (for discard/swap/pair_claim)
                                        if (actionGame.CaboPending)
                                        {
                                            actionGame.CaboCountdown -= 1;
                                            if (actionGame.CaboCountdown <= 0)
                                            {
                                                actionGame.GameOver = true;
                                                var totals = actionGame.Players.Select(pl => new { id = pl.Id, total = pl.Hand.Sum(h => int.TryParse(h, out var v) ? v : 0) }).ToList();
                                                var winner = totals.OrderBy(t => t.total).First();
                                                actionGame.Winner = winner.id;
                                                await SendGameUpdate(actionGame, "Final scoring: " + string.Join(", ", totals.Select(t => t.id + ":" + t.total)));
                                                break;
                                            }
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
                                        // mark replaced card face-up
                                        if (cardIdx >= 0 && cardIdx < actingPlayer.Revealed.Count)
                                        {
                                            actingPlayer.Revealed[cardIdx] = true;
                                        }
                                        // make replaced card pending to allow swap back
                                        actionGame.PendingCard = replaced;
                                        actionGame.PendingPlayerId = actingPlayer.Id;
                                        actionGame.DiscardPile.Add(replaced ?? string.Empty);
                                        actionGame.LastAction = new { type = "take_discard", player = actingPlayer.Id, card = top ?? string.Empty, cardIndex = cardIdx };
                                        // replay draw offer
                                        if (_connections.TryGetValue(actingPlayer.Id, out var replayWs2) && replayWs2.State == WebSocketState.Open)
                                        {
                                            await SendJson(replayWs2, new { type = "draw_offer", card = replaced });
                                        }
                                        // keep turn same (still drawing phase)
                                        if (actionGame.CaboPending)
                                        {
                                            actionGame.CaboCountdown -= 1;
                                            if (actionGame.CaboCountdown <= 0)
                                            {
                                                actionGame.GameOver = true;
                                                var totals = actionGame.Players.Select(pl => new { id = pl.Id, total = pl.Hand.Sum(h => int.TryParse(h, out var v) ? v : 0) }).ToList();
                                                var winner = totals.OrderBy(t => t.total).First();
                                                actionGame.Winner = winner.id;
                                                await SendGameUpdate(actionGame, "Final scoring: " + string.Join(", ", totals.Select(t => t.id + ":" + t.total)));
                                                break;
                                            }
                                        }
                                        await SendGameUpdate(actionGame, null);
                                        break;
                                    }
                                    case "call_cabo":
                                    {
                                        // only allow calling Cabo on your turn
                                        if (actionGame.CurrentTurn != playerIdx) { await SendError(webSocket, "You can only call Cabo on your turn"); break; }
                                        actionGame.CaboPending = true;
                                        actionGame.CaboCountdown = actionGame.Players.Count - 1; // others get one more turn each
                                        actionGame.LastAction = new { type = "call_cabo", player = actingPlayer.Id };
                                        // advance to next player
                                        actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                                        await SendGameUpdate(actionGame, "Cabo called by " + actingPlayer.Id);
                                        break;
                                    }
                                }
                                break;
                            }
                            case "chat_message":
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

        // helper previously used for masking; no longer needed
        // (we send full hand to clients and let them decide visibility)
        // private List<object> BuildPlayersDto(...) { ... }

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

        private async Task SendGameUpdate(GameState gameState, string? result)
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
