using System.Collections.Frozen;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Tests.Runtime;

/// <summary>
/// Threads mark parent gains of registrations and route graph changes concurrently, while other registrations gain
/// parents as they are disposed. Every gain must reach its listener with a graph change routed after it, and the number
/// of marks, which a writer reads before the marks, must never fall below zero and must return to zero.
/// </summary>
[Trait("Category", "Integration")]
public class AutomationChangeDispatcherStressTests
{
    private const int GainerCount = 4;
    private const int NotifierCount = 2;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WhenParentGainsGraphChangesAndUnregistrationsRace_ThenEveryGainReachesItsListenerAndTheCountReturnsToZero(int seed)
    {
        // Arrange
        using var race = new GainRace();

        // Act
        var gainers = Enumerable.Range(0, GainerCount).Select(gainer => Task.Run(() => race.Gain(new Random(seed * 100 + gainer)))).ToArray();
        var notifiers = Enumerable.Range(0, NotifierCount).Select(_ => Task.Run(race.Notify)).ToArray();
        var churn = Task.Run(() => race.ChurnAsync(new Random(seed)));
        await Task.WhenAll(gainers);
        race.StopGaining();
        await Task.WhenAll([.. notifiers, churn]);
        race.Settle();

        // Assert
        Assert.Equal(0, race.LostGains);
        Assert.True(race.LowestCount >= 0, $"The count fell to {race.LowestCount}.");
        Assert.Equal(0, race.Count);
    }

    private sealed class GainRace : IDisposable
    {
        private const int ListenerCount = 8;
        private const int GainsPerGainer = 20_000;
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

        private readonly TestGraph _graph = new();
        private readonly AutomationChangeDispatcher _dispatcher = new();
        private readonly PropertyReference _added;
        private readonly FrozenSet<PropertyReference> _route;
        private readonly CountingListener[] _listeners;
        private readonly AutomationChangeDispatcher.Registration[] _registrations;
        private readonly AutomationChangeDispatcher.Registration _keeper;
        private readonly AutomationChangeDispatcher.ChangeSource _source;
        private int _lowestCount;
        private int _lostGains;
        private int _isGaining = 1;

        public GainRace()
        {
            var weather = new TestSensor();
            _graph.Add("Weather", weather);
            _added = new PropertyReference(weather, "Added");
            _route = new[] { new PropertyReference(weather, nameof(TestSensor.Gust)) }.ToFrozenSet();
            _listeners = [.. Enumerable.Range(0, ListenerCount).Select(_ => new CountingListener())];
            _registrations = [.. _listeners.Select(Register)];

            // Keeps the change stream subscribed while the other registrations come and go.
            _keeper = Register(new CountingListener());
            _source = _keeper.Source;
            _source.ParentGainCountChanging = _ => LowerTo(ref _lowestCount, _source.ParentGainCount);
        }

        public int LostGains => Volatile.Read(ref _lostGains);

        public int LowestCount => Volatile.Read(ref _lowestCount);

        public int Count => _source.ParentGainCount;

        public void Gain(Random random)
        {
            for (var gain = 0; gain < GainsPerGainer; gain++)
            {
                var index = random.Next(ListenerCount);
                var listener = _listeners[index];
                var graphChangesBefore = listener.GraphChanges;
                _registrations[index].OnParentGained();

                // As the write that added the parent; a notification on another thread may take the mark first and give
                // the graph change a moment later.
                _source.OnPropertyAdded(_added);
                if (!SpinWait.SpinUntil(() => listener.GraphChanges > graphChangesBefore, Bound))
                {
                    Interlocked.Increment(ref _lostGains);
                }
            }
        }

        public void Notify()
        {
            while (Volatile.Read(ref _isGaining) == 1)
            {
                _source.OnPropertyAdded(_added);
            }
        }

        public async Task ChurnAsync(Random random)
        {
            while (Volatile.Read(ref _isGaining) == 1)
            {
                var registration = Register(new CountingListener());
                var gains = random.Next(1, 4);
                var gaining = Task.Run(() =>
                {
                    for (var gain = 0; gain < gains; gain++)
                    {
                        registration.OnParentGained();
                    }
                });
                registration.Dispose();
                await gaining;
                registration.OnParentGained();
            }
        }

        public void StopGaining() => Volatile.Write(ref _isGaining, 0);

        /// <summary>Stops recording the lowest count and routes one more graph change, which takes every mark still set.</summary>
        public void Settle()
        {
            _source.ParentGainCountChanging = null;
            _source.OnPropertyAdded(_added);
        }

        public void Dispose()
        {
            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }

            _keeper.Dispose();
        }

        private AutomationChangeDispatcher.Registration Register(CountingListener listener)
        {
            var registration = _dispatcher.Register(_graph.Context, listener);
            registration.PublishRoute(_route, isUnbounded: false);
            return registration;
        }

        private static void LowerTo(ref int lowest, int value)
        {
            var current = Volatile.Read(ref lowest);
            while (value < current)
            {
                var observed = Interlocked.CompareExchange(ref lowest, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class CountingListener : IAutomationChangeListener
    {
        private int _graphChanges;

        public int GraphChanges => Volatile.Read(ref _graphChanges);

        public void OnWatchedPropertyChanged(PropertyChangedInput input)
        {
            // No property is watched.
        }

        public void OnGraphChanged(int chainDepth) => Interlocked.Increment(ref _graphChanges);
    }
}
