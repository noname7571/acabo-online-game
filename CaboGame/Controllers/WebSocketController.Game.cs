using System.Net.WebSockets;
using System.Text.Json;
using CaboGame.Game;
using CaboGame.Game.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace CaboGame.Controllers
{
    public partial class WebSocketController
    {
        private async Task HandlePlayerAction(JsonElement root, string? playerId, WebSocket webSocket)
        {
            if (playerId == null) return;
            var actionLobbyId = root.GetProperty("lobbyId").GetString() ?? string.Empty;
            var actionType = root.GetProperty("actionType").GetString() ?? string.Empty;
            var payload = root.TryGetProperty("payload", out var p) ? p : default;
            if (!_gameManager.Games.TryGetValue(actionLobbyId, out var actionGame)) return;
            var playerIdx = actionGame.Players.FindIndex(pp => pp.Id == playerId);
            if (playerIdx == -1 || actionGame.GameOver) return;
            var actingPlayer = actionGame.Players[playerIdx];

            switch (actionType)
            {
                case "update_settings":
                    await HandleUpdateSettings(actionLobbyId, payload);
                    break;
                case "skip_turn":
                    await HandleSkipTurn(actionGame, playerIdx);
                    break;
                case "peek_initial":
                    await HandlePeekInitial(actionGame, actingPlayer, webSocket, payload);
                    break;
                case "draw":
                    await HandleDraw(actionGame, actingPlayer);
                    break;
                case "resolve_draw":
                    await HandleResolveDraw(actionGame, actingPlayer, webSocket, payload);
                    break;
                case "take_discard":
                    await HandleTakeDiscard(actionGame, actingPlayer, payload);
                    break;
                case "call_cabo":
                    await HandleCallCabo(actionGame, actingPlayer, playerIdx, webSocket);
                    break;
            }
        }

        // individual helper methods for each action to keep logic manageable
        private Task HandleUpdateSettings(string actionLobbyId, JsonElement payload)
        {
            var timerEnabled = payload.GetProperty("timerEnabled").GetBoolean();
            var turnSeconds = payload.GetProperty("turnSeconds").GetInt32();
            if (_lobbyManager.Lobbies.TryGetValue(actionLobbyId, out var lo))
            {
                lo.TimerEnabled = timerEnabled;
                lo.TurnSeconds = turnSeconds;
                return SendLobbyUpdate(lo);
            }
            return Task.CompletedTask;
        }

        private async Task HandleSkipTurn(GameState actionGame, int playerIdx)
        {
            if (actionGame.CurrentTurn == playerIdx && actionGame.Phase == "draw")
            {
                actionGame.PendingCard = null;
                actionGame.PendingPlayerId = null;
                actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                await SendGameUpdate(actionGame, "turn skipped");
            }
        }

        private async Task HandlePeekInitial(GameState actionGame, Player actingPlayer, WebSocket webSocket, JsonElement payload)
        {
            if (actingPlayer.InitialPeeksRemaining <= 0) { await SendError(webSocket, "No peeks remaining"); return; }
            int idx = payload.GetProperty("index").GetInt32();
            if (idx < 0 || idx >= actingPlayer.Hand.Count) { await SendError(webSocket, "Invalid index"); return; }
            var card = actingPlayer.Hand[idx];
            actingPlayer.InitialPeeksRemaining -= 1;
            await SendJson(webSocket, new { type = "peek_result", index = idx, card });
            await SendGameUpdate(actionGame, null);
        }

        private async Task HandleDraw(GameState actionGame, Player actingPlayer)
        {
            if (actionGame.Phase != "draw" || actionGame.CurrentTurn != actionGame.Players.IndexOf(actingPlayer)) return;
            if (actionGame.PendingCard != null)
            {
                if (_connections.TryGetValue(actingPlayer.Id, out var ws) && ws.State == WebSocketState.Open)
                    await SendError(ws, "You already drew a card");
                return;
            }
            if (actionGame.Deck.Count == 0)
            {
                if (_connections.TryGetValue(actingPlayer.Id, out var ws) && ws.State == WebSocketState.Open)
                    await SendError(ws, "Deck is empty");
                return;
            }
            var card = actionGame.Deck[0];
            actionGame.Deck.RemoveAt(0);
            actionGame.PendingCard = card;
            actionGame.PendingPlayerId = actingPlayer.Id;
            if (_connections.TryGetValue(actingPlayer.Id, out var ws2) && ws2.State == WebSocketState.Open)
            {
                await SendJson(ws2, new { type = "draw_offer", card });
            }
            await SendGameUpdate(actionGame, null);
        }

        private async Task HandleResolveDraw(GameState actionGame, Player actingPlayer, WebSocket webSocket, JsonElement payload)
        {
            if (actionGame.PendingPlayerId != actingPlayer.Id) return;
            var action = payload.GetProperty("action").GetString() ?? string.Empty;
            string? drawn = actionGame.PendingCard;
            actionGame.PendingCard = null;
            actionGame.PendingPlayerId = null;
            switch (action)
            {
                case "discard":
                    actionGame.DiscardPile.Add(drawn ?? string.Empty);
                    actionGame.LastAction = new { type = "discard", player = actingPlayer.Id, card = drawn ?? string.Empty };
                    actionGame.Phase = "draw";
                    actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                    PostAdvance(actionGame);
                    break;
                case "swap":
                    int cardIdx = payload.GetProperty("cardIndex").GetInt32();
                    var replaced = actingPlayer.Hand[cardIdx];
                    actingPlayer.Hand[cardIdx] = drawn ?? string.Empty;
                    actionGame.DiscardPile.Add(replaced ?? string.Empty);
                    actionGame.LastAction = new { type = "swap", player = actingPlayer.Id, card = drawn ?? string.Empty };
                    actionGame.Phase = "draw";
                    actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
                    break;
                case "pair_claim":
                    await ResolvePairClaim(actionGame, actingPlayer, drawn, payload);
                    break;
                case "use_ability":
                    await ResolveAbility(actionGame, actingPlayer, drawn, payload);
                    break;
            }

            if (actionGame.CaboPending)
            {
                actionGame.CaboCountdown -= 1;
                if (actionGame.CaboCountdown <= 0)
                {
                    await FinishGameDueToCabo(actionGame);
                    return;
                }
            }

            await SendGameUpdate(actionGame, null);
        }

        private Task ResolvePairClaim(GameState actionGame, Player actingPlayer, string? drawn, JsonElement payload)
        {
            if (!payload.TryGetProperty("indices", out var idxArr) || idxArr.GetArrayLength() < 2)
            {
                return Task.CompletedTask;
            }
            var indices = new List<int>();
            for (int ii = 0; ii < idxArr.GetArrayLength(); ii++)
                indices.Add(idxArr[ii].GetInt32());
            if (indices.Any(i => i < 0 || i >= actingPlayer.Hand.Count))
            {
                return Task.CompletedTask;
            }
            var firstVal = actingPlayer.Hand[indices[0]];
            if (indices.All(i => actingPlayer.Hand[i] == firstVal))
            {
                foreach (var i in indices.OrderByDescending(x => x))
                {
                    actingPlayer.Hand.RemoveAt(i);
                    actingPlayer.Revealed.RemoveAt(i);
                }
                foreach (var _ in indices) actionGame.DiscardPile.Add(firstVal ?? string.Empty);
                actingPlayer.Hand.Add(drawn ?? string.Empty);
                actingPlayer.Revealed.Add(false);
                actionGame.LastAction = new { type = "pair_claim_success", player = actingPlayer.Id, card = drawn ?? string.Empty, indices };
                actionGame.Phase = "draw";
                actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
            }
            else
            {
                actingPlayer.Hand.Add(drawn ?? string.Empty);
                actingPlayer.Revealed.Add(false);
                actionGame.LastAction = new { type = "pair_claim_fail", player = actingPlayer.Id, card = drawn ?? string.Empty, indices };
                actionGame.Phase = "draw";
                actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
            }
            return Task.CompletedTask;
        }

        private async Task ResolveAbility(GameState actionGame, Player actingPlayer, string? drawn, JsonElement payload)
        {
            var targetPlayerId = payload.GetProperty("targetPlayer").GetString() ?? string.Empty;
            var targetIdx = payload.GetProperty("targetIdx").GetInt32();
            var cardVal = int.TryParse(drawn, out var cv) ? cv : -1;
            if (cardVal >= 7 && cardVal <= 8)
            {
                await SendAbilityPeek(actionGame, actingPlayer.Id, targetPlayerId, targetIdx);
            }
            else if (cardVal >= 9 && cardVal <= 10)
            {
                await SendAbilityPeek(actionGame, actingPlayer.Id, targetPlayerId, targetIdx, "ability_spy");
            }
            else if (cardVal >= 11 && cardVal <= 12)
            {
                var tp = actionGame.Players.FirstOrDefault(x => x.Id == targetPlayerId);
                if (tp != null)
                {
                    int actorIdx = payload.GetProperty("actorIdx").GetInt32();
                    var tmp = actingPlayer.Hand[actorIdx];
                    actingPlayer.Hand[actorIdx] = tp.Hand[targetIdx];
                    tp.Hand[targetIdx] = tmp;
                }
            }

            actionGame.DiscardPile.Add(drawn ?? string.Empty);
            actionGame.LastAction = new { type = "ability_used", player = actingPlayer.Id, card = drawn ?? string.Empty };
            actionGame.Phase = "draw";
            actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
        }

        private async Task SendAbilityPeek(GameState actionGame, string actorId, string targetPlayerId, int targetIdx, string messageType = "ability_peek")
        {
            var tp = actionGame.Players.FirstOrDefault(x => x.Id == targetPlayerId);
            if (tp != null)
            {
                var revealedCard = tp.Hand[targetIdx];
                if (_connections.TryGetValue(actorId, out var aws) && aws.State == WebSocketState.Open)
                {
                    await SendJson(aws, new { type = messageType, targetPlayer = targetPlayerId, index = targetIdx, card = revealedCard });
                }
            }
        }

        private async Task HandleTakeDiscard(GameState actionGame, Player actingPlayer, JsonElement payload)
        {
            if (actionGame.PendingCard != null && actionGame.PendingPlayerId == actingPlayer.Id) { return; }
            if (actionGame.DiscardPile.Count == 0) { return; }
            int cardIdx = payload.GetProperty("cardIndex").GetInt32();
            var top = actionGame.DiscardPile.Last();
            actionGame.DiscardPile.RemoveAt(actionGame.DiscardPile.Count - 1);
            var replaced = actingPlayer.Hand[cardIdx];
            actingPlayer.Hand[cardIdx] = top;
            if (cardIdx >= 0 && cardIdx < actingPlayer.Revealed.Count)
            {
                actingPlayer.Revealed[cardIdx] = true;
            }
            actionGame.DiscardPile.Add(replaced ?? string.Empty);
            actionGame.LastAction = new { type = "take_discard", player = actingPlayer.Id, card = top ?? string.Empty, cardIndex = cardIdx };
            actionGame.Phase = "draw";
            actionGame.CurrentTurn = (actionGame.CurrentTurn + 1) % actionGame.Players.Count;
            PostAdvance(actionGame);
            if (actionGame.CaboPending)
            {
                actionGame.CaboCountdown -= 1;
                if (actionGame.CaboCountdown <= 0)
                {
                    await FinishGameDueToCabo(actionGame);
                    return;
                }
            }
            await SendGameUpdate(actionGame, null);
        }

        private async Task HandleCallCabo(GameState actionGame, Player actingPlayer, int playerIdx, WebSocket webSocket)
        {
            if (actionGame.PendingCard != null && actionGame.PendingPlayerId == actingPlayer.Id)
            {
                await SendError(webSocket, "Resolve your drawn card first");
                return;
            }
            if (actionGame.CurrentTurn != playerIdx) { await SendError(webSocket, "You can only call Cabo on your turn"); return; }
            actionGame.CaboCalledBy = actingPlayer.Id;
            actionGame.LastAction = new { type = "call_cabo_requested", player = actingPlayer.Id };
            await SendGameUpdate(actionGame, "Cabo will be called by " + actingPlayer.Id + " at end of turn");
        }

        // common helpers moved here
        private async Task FinishGameDueToCabo(GameState actionGame)
        {
            actionGame.GameOver = true;
            var totals = actionGame.Players.Select(pl => new { id = pl.Id, total = pl.Hand.Sum(h => int.TryParse(h, out var v) ? v : 0) }).ToList();
            var winner = totals.OrderBy(t => t.total).First();
            actionGame.Winner = winner.id;
            actionGame.Totals = totals.ToDictionary(t => t.id, t => t.total);
            await SendGameUpdate(actionGame, "Final scoring: " + string.Join(", ", totals.Select(t => t.id + ":" + t.total)));
        }

        private async Task SendGameStart(GameState gameState)
        {
            Lobby? lobby = null;
            if (gameState.LobbyId != null && _lobbyManager.Lobbies.TryGetValue(gameState.LobbyId, out var lb)) lobby = lb;
            var playersDto = gameState.Players.Select(p => new { id = p.Id, name = p.Name, hand = p.Hand, revealed = p.Revealed, initialPeeksRemaining = p.InitialPeeksRemaining }).ToList();
            foreach (var player in gameState.Players)
            {
                if (_connections.TryGetValue(player.Id, out var ws) && ws.State == WebSocketState.Open)
                {
                    var dto = new
                    {
                        type = "game_start",
                        lobbyId = gameState.LobbyId,
                        timerEnabled = lobby?.TimerEnabled ?? false,
                        turnSeconds = lobby?.TurnSeconds ?? 15,
                        gameState = new
                        {
                            lobbyId = gameState.LobbyId,
                            players = playersDto,
                            deck = gameState.Deck,
                            discardPile = gameState.DiscardPile,
                            currentTurn = gameState.CurrentTurn,
                            phase = gameState.Phase,
                            gameOver = gameState.GameOver,
                            winner = gameState.Winner,
                            totals = gameState.Totals,
                            pendingPlayerId = gameState.PendingPlayerId,
                            pendingCard = gameState.PendingCard
                        },
                        selfPlayerId = player.Id
                    };
                    await SendJson(ws, dto);
                }
            }
        }

        private async Task SendGameUpdate(GameState gameState, string? result)
        {
            Lobby? lobby = null;
            if (gameState.LobbyId != null && _lobbyManager.Lobbies.TryGetValue(gameState.LobbyId, out var lb)) lobby = lb;
            var playersDto = gameState.Players.Select(p => new { id = p.Id, name = p.Name, hand = p.Hand, revealed = p.Revealed, initialPeeksRemaining = p.InitialPeeksRemaining }).ToList();
            foreach (var player in gameState.Players)
            {
                if (_connections.TryGetValue(player.Id, out var ws) && ws.State == WebSocketState.Open)
                {
                    var dto = new
                    {
                        type = "game_update",
                        lobbyId = gameState.LobbyId,
                        timerEnabled = lobby?.TimerEnabled ?? false,
                        turnSeconds = lobby?.TurnSeconds ?? 15,
                        gameState = new
                        {
                            lobbyId = gameState.LobbyId,
                            players = playersDto,
                            deck = gameState.Deck,
                            discardPile = gameState.DiscardPile,
                            currentTurn = gameState.CurrentTurn,
                            phase = gameState.Phase,
                            gameOver = gameState.GameOver,
                            winner = gameState.Winner,
                            totals = gameState.Totals,
                            pendingPlayerId = gameState.PendingPlayerId,
                            pendingCard = gameState.PendingCard
                        },
                        result,
                        selfPlayerId = player.Id
                    };
                    await SendJson(ws, dto);
                }
            }
        }

        private void PostAdvance(GameState gameState)
        {
            if (!string.IsNullOrEmpty(gameState.CaboCalledBy))
            {
                gameState.CaboPending = true;
                gameState.CaboCountdown = gameState.Players.Count - 1;
                gameState.LastAction = new { type = "cabo_called", player = gameState.CaboCalledBy };
                gameState.CaboCalledBy = null;
            }
        }

        private List<string> GenerateDeck()
        {
            var deck = new List<string>();
            deck.AddRange(Enumerable.Repeat("0", 2));
            for (int v = 1; v <= 12; v++)
            {
                deck.AddRange(Enumerable.Repeat(v.ToString(), 4));
            }
            deck.AddRange(Enumerable.Repeat("13", 2));
            var rng = new Random();
            return deck.OrderBy(_ => rng.Next()).ToList();
        }
    }
}
