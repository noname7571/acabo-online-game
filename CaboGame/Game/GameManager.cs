using System.Collections.Generic;

namespace CaboGame.Game
{
using CaboGame.Game.Models;
    public class GameManager
    {
        public Dictionary<string, GameState> Games { get; } = new();
    }

    public class GameState
    {
        public string LobbyId { get; set; }
        public List<Player> Players { get; set; } = new();
        public List<string> Deck { get; set; } = new();
        public List<string> DiscardPile { get; set; } = new();
        public int CurrentTurn { get; set; }
        public string Phase { get; set; }
        public object LastAction { get; set; }
        public string PendingCard { get; set; }
        public string PendingPlayerId { get; set; }
        public bool GameOver { get; set; }
        public string Winner { get; set; }
    }
}
