using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CaboGame.Game
{
using CaboGame.Game.Models;
    public class LobbyManager
    {
        public ConcurrentDictionary<string, Lobby> Lobbies { get; } = new();
    }

    public class Lobby
    {
        public string LobbyId { get; set; } = string.Empty;
        public List<Player> Players { get; set; } = new();
        public bool GameStarted { get; set; } = false;
        // per-lobby turn timer configuration
        public bool TimerEnabled { get; set; } = true;
        public int TurnSeconds { get; set; } = 15;
    }
}
