namespace CaboGame.Game.Models
{
    public class Player
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string[] Hand { get; set; } = new string[4];
        public bool[] Revealed { get; set; } = new bool[4];
        public bool IsConnected { get; set; } = true;
        public int Score { get; set; } = 0;
        public int InitialPeeksRemaining { get; set; } = 2;
    }
}
