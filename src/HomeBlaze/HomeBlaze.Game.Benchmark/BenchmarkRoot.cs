using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Game.Benchmark;

/// <summary>
/// The root that holds the game, as a storage does in the app.
/// </summary>
[InterceptorSubject]
public partial class BenchmarkRoot
{
    public partial Game? Game { get; set; }

    public BenchmarkRoot()
    {
        Game = null;
    }
}
