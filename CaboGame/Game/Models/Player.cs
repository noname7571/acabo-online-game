namespace CaboGame.Game.Models
{
    public class Player
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public System.Collections.Generic.List<string> Hand { get; set; } = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<bool> Revealed { get; set; } = new System.Collections.Generic.List<bool>();
        public bool IsConnected { get; set; } = true;
        public int Score { get; set; } = 0;
        public int InitialPeeksRemaining { get; set; } = 2;
    }
}
