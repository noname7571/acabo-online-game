namespace CaboGame.Game.Models
{
    public class MessageTypes
    {
        public const string CreateLobby = "create_lobby";
        public const string JoinLobby = "join_lobby";
        public const string LeaveLobby = "leave_lobby";
        public const string StartGame = "start_game";
        public const string PlayerAction = "player_action";
        public const string ChatMessage = "chat_message";
        public const string LobbyList = "lobby_list";
        public const string LobbyUpdate = "lobby_update";
        public const string GameStart = "game_start";
        public const string GameUpdate = "game_update";
        public const string ActionResult = "action_result";
        public const string Error = "error";
    }
}
