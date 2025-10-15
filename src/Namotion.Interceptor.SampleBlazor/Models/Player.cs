using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.SampleBlazor.Models
{
    [InterceptorSubject]
    public partial class Player : IDisposable
    {
        private readonly Game _game;

        public partial string Name { get; set; }

        public Player(Game game)
        {
            Name = Guid.NewGuid().ToString();

            _game = game;
            _game.AddPlayer(this);
        }

        public void Dispose()
        {
            _game.RemovePlayer(this);
        }
    }
}
