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
        public string LobbyId { get; set; }
        public List<Player> Players { get; set; } = new();
        public bool GameStarted { get; set; } = false;
    }
}
