namespace Namotion.Proxy.SampleBlazor.Models
{
    [GenerateProxy]
    public abstract class GameBase
    {
        public virtual Player[] Players { get; protected set; } = [];

        public void AddPlayer(Player player)
        {
            lock (this)
                Players = [.. Players, player];
        }

        public void RemovePlayer(Player player)
        {
            lock (this)
                Players = Players.Where(p => p != player).ToArray();
        }
    }
}
