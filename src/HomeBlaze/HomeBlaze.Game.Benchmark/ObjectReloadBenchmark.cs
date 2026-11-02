using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace HomeBlaze.Game.Benchmark;

/// <summary>
/// A reload of the 100k objects of a game with 200 players and their view sessions, as a storage makes it: new regions
/// with new subjects written to <see cref="Game.Regions"/> outside the simulation, which the next tick takes over.
/// Each invocation needs a new set, so every iteration runs one invocation.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class ObjectReloadBenchmark
{
    private const int PlayerCount = 200;
    private const int ObjectCount = 100_000;

    private BenchmarkWorld _world = null!;
    private Dictionary<string, GameRegion> _regions = null!;

    [GlobalSetup]
    public void Setup() => _world = BenchmarkWorld.Create(PlayerCount, ObjectCount);

    [IterationSetup]
    public void CreateRegions() => _regions = GameRegion.CreateRegions(_world.CreateObjectSet());

    // Takes over the set the write-only benchmark wrote, so every iteration starts from a settled world.
    [IterationCleanup(Target = nameof(WriteObjects))]
    public void TakeOverObjects() => _world.Tick();

    /// <summary>
    /// The write of the new regions on the caller's thread: attaches the new subjects, detaches the former ones and posts
    /// the take-over to the simulation.
    /// </summary>
    [Benchmark]
    public void WriteObjects() => _world.Game.Regions = _regions;

    /// <summary>
    /// The write of the new regions and the tick that takes them over: indexes the new objects, publishes the region set
    /// and updates every view session, all of whose chunks changed.
    /// </summary>
    [Benchmark]
    public void WriteObjectsAndTick()
    {
        _world.Game.Regions = _regions;
        _world.Tick();
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default.WithLaunchCount(1).WithWarmupCount(2).WithIterationCount(10).WithInvocationCount(1).WithUnrollFactor(1));
        }
    }
}
