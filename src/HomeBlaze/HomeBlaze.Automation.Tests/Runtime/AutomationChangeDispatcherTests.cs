using System.Collections.Concurrent;
using System.Collections.Frozen;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Tests.Runtime;

public class AutomationChangeDispatcherTests
{
    [Fact]
    public void WhenAPropertyIsWatched_ThenOnlyItsWatcherReceivesItsChanges()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var gustWatcher = new RecordingListener();
        var rainWatcher = new RecordingListener();
        using var gustRegistration = dispatcher.Register(graph.Context, gustWatcher);
        using var rainRegistration = dispatcher.Register(graph.Context, rainWatcher);
        gustRegistration.PublishWatched(Watch(Gust(weather)));
        rainRegistration.PublishWatched(Watch(Rain(weather)));

        // Act
        weather.Gust = 9;

        // Assert
        var change = Assert.Single(gustWatcher.Changes);
        Assert.Equal(Gust(weather), change.Property);
        Assert.Equal(9.0, change.Value);
        Assert.Equal(0, change.ChainDepth);
        Assert.Empty(rainWatcher.Changes);
        Assert.Equal(0, gustWatcher.GraphChanges);
    }

    [Fact]
    public void WhenSeveralListenersWatchAProperty_ThenEachReceivesTheChangeOnce()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var first = new RecordingListener();
        var second = new RecordingListener();
        using var firstRegistration = dispatcher.Register(graph.Context, first);
        using var secondRegistration = dispatcher.Register(graph.Context, second);
        firstRegistration.PublishWatched(Watch(Gust(weather)));
        secondRegistration.PublishWatched(Watch(Gust(weather), Rain(weather)));

        // Act
        weather.Gust = 9;

        // Assert
        Assert.Equal(9.0, Assert.Single(first.Changes).Value);
        Assert.Equal(9.0, Assert.Single(second.Changes).Value);
        Assert.Equal(2, dispatcher.CountWatchers(graph.Context, Gust(weather)));
        Assert.Equal(1, dispatcher.CountWatchers(graph.Context, Rain(weather)));
    }

    [Fact]
    public void WhenTheWatchedSetChanges_ThenChangesAreRoutedByTheNewSet()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var listener = new RecordingListener();
        using var registration = dispatcher.Register(graph.Context, listener);
        registration.PublishWatched(Watch(Gust(weather)));

        // Act
        registration.PublishWatched(Watch(Rain(weather)));
        weather.Gust = 9;
        weather.Rain = 2;

        // Assert
        Assert.Equal(Rain(weather), Assert.Single(listener.Changes).Property);
        Assert.Equal(0, dispatcher.CountWatchers(graph.Context, Gust(weather)));
        Assert.Equal(1, dispatcher.CountWatchers(graph.Context, Rain(weather)));
    }

    [Fact]
    public void WhenAPropertyThatHoldsSubjectsChanges_ThenEveryListenerIsNotifiedOnce()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var watcher = new RecordingListener();
        var idle = new RecordingListener();
        using var watcherRegistration = dispatcher.Register(graph.Context, watcher);
        using var idleRegistration = dispatcher.Register(graph.Context, idle);
        watcherRegistration.PublishWatched(Watch(Gust(weather)));

        // Act
        weather.Gust = 9;
        graph.Add("Garden", new TestSensor());

        // Assert
        Assert.Equal(1, watcher.GraphChanges);
        Assert.Equal(1, idle.GraphChanges);
        Assert.Single(watcher.Changes);
        Assert.Empty(idle.Changes);
    }

    [Fact]
    public void WhenAListenerPublishesARoute_ThenOnlyAChangeOfAPropertyOnItIsAGraphChange()
    {
        // Arrange
        var (graph, _) = CreateGraph();
        var holder = new TestSensorHolder();
        graph.Add("Holder", holder);
        var dispatcher = new AutomationChangeDispatcher();
        var routed = new RecordingListener();
        var unrouted = new RecordingListener();
        using var routedRegistration = dispatcher.Register(graph.Context, routed);
        using var unroutedRegistration = dispatcher.Register(graph.Context, unrouted);
        routedRegistration.PublishRoute(Watch(SensorOf(holder)), isUnbounded: false);

        // Act
        graph.Add("Garden", new TestSensor());
        var graphChangesAfterAnotherProperty = routed.GraphChanges;
        holder.Sensor = new TestSensor();

        // Assert
        Assert.Equal(0, graphChangesAfterAnotherProperty);
        Assert.Equal(1, routed.GraphChanges);
        Assert.Equal(2, unrouted.GraphChanges);
        Assert.True(dispatcher.IsRoutedAsGraphChange(graph.Context, routed, SensorOf(holder)));
        Assert.False(dispatcher.IsRoutedAsGraphChange(graph.Context, routed, ChildrenOf(graph)));
        Assert.True(dispatcher.IsRoutedAsGraphChange(graph.Context, unrouted, ChildrenOf(graph)));
    }

    [Fact]
    public void WhenARouteIsUnbounded_ThenAChangeOfAPropertyOnItAndOfEveryPropertyThatHoldsSubjectsIsAGraphChange()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var listener = new RecordingListener();
        using var registration = dispatcher.Register(graph.Context, listener);
        registration.PublishRoute(Watch(Gust(weather)), isUnbounded: true);

        // Act
        weather.Gust = 9;
        weather.Rain = 2;
        graph.Add("Garden", new TestSensor());

        // Assert
        Assert.Equal(2, listener.GraphChanges);
        Assert.Empty(listener.Changes);
    }

    [Fact]
    public void WhenAListenerWithARouteIsUnregistered_ThenItsRouteIsRemoved()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var holder = new TestSensorHolder();
        graph.Add("Holder", holder);
        var dispatcher = new AutomationChangeDispatcher();
        var leaving = new RecordingListener();
        var staying = new RecordingListener();
        var leavingRegistration = dispatcher.Register(graph.Context, leaving);
        using var stayingRegistration = dispatcher.Register(graph.Context, staying);
        leavingRegistration.PublishRoute(Watch(SensorOf(holder), Gust(weather)), isUnbounded: false);
        stayingRegistration.PublishRoute(Watch(SensorOf(holder)), isUnbounded: false);

        // Act
        leavingRegistration.Dispose();
        leavingRegistration.PublishRoute(Watch(Gust(weather)), isUnbounded: true);
        holder.Sensor = new TestSensor();
        weather.Gust = 9;

        // Assert
        Assert.Equal(0, leaving.GraphChanges);
        Assert.Equal(1, staying.GraphChanges);
        Assert.False(dispatcher.IsRoutedAsGraphChange(graph.Context, leaving, SensorOf(holder)));
        Assert.True(dispatcher.IsRoutedAsGraphChange(graph.Context, staying, SensorOf(holder)));
    }

    [Fact]
    public void WhenAChangeIsWrittenDuringARun_ThenItCarriesTheChainDepthOfTheWritingFlow()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var listener = new RecordingListener();
        using var registration = dispatcher.Register(graph.Context, listener);
        registration.PublishWatched(Watch(Gust(weather)));

        // Act
        using (AutomationInputQueue.EnterRun(3))
        {
            weather.Gust = 9;
            graph.Add("Garden", new TestSensor());
        }

        // Assert
        Assert.Equal(4, Assert.Single(listener.Changes).ChainDepth);
        Assert.Equal([4], listener.GraphChainDepths);
    }

    [Fact]
    public void WhenAListenerIsUnregistered_ThenItReceivesNoMoreChanges()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var leaving = new RecordingListener();
        var staying = new RecordingListener();
        var leavingRegistration = dispatcher.Register(graph.Context, leaving);
        using var stayingRegistration = dispatcher.Register(graph.Context, staying);
        leavingRegistration.PublishWatched(Watch(Gust(weather)));
        stayingRegistration.PublishWatched(Watch(Gust(weather)));

        // Act
        leavingRegistration.Dispose();
        leavingRegistration.PublishWatched(Watch(Gust(weather), Rain(weather)));
        weather.Gust = 9;
        weather.Rain = 2;
        graph.Add("Garden", new TestSensor());

        // Assert
        Assert.Empty(leaving.Changes);
        Assert.Equal(0, leaving.GraphChanges);
        Assert.Single(staying.Changes);
        Assert.Equal(1, staying.GraphChanges);
        Assert.Equal(1, dispatcher.CountWatchers(graph.Context, Gust(weather)));
        Assert.Equal(0, dispatcher.CountWatchers(graph.Context, Rain(weather)));
    }

    [Fact]
    public void WhenTheLastListenerOfAContextIsUnregistered_ThenTheDispatcherReleasesTheContext()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var first = dispatcher.Register(graph.Context, new RecordingListener());
        var second = dispatcher.Register(graph.Context, new RecordingListener());
        first.PublishWatched(Watch(Gust(weather)));
        var sourcesWhileRegistered = dispatcher.SourceCount;

        // Act
        first.Dispose();
        second.Dispose();
        second.Dispose();

        // Assert
        Assert.Equal(1, sourcesWhileRegistered);
        Assert.Equal(0, dispatcher.SourceCount);
        Assert.Equal(0, dispatcher.CountWatchers(graph.Context, Gust(weather)));
    }

    [Fact]
    public void WhenAListenerRegistersAgainAfterTheLastOneLeft_ThenItReceivesChanges()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        dispatcher.Register(graph.Context, new RecordingListener()).Dispose();
        var listener = new RecordingListener();

        // Act
        using var registration = dispatcher.Register(graph.Context, listener);
        registration.PublishWatched(Watch(Gust(weather)));
        weather.Gust = 9;

        // Assert
        Assert.Single(listener.Changes);
        Assert.Equal(1, dispatcher.SourceCount);
    }

    [Fact]
    public void WhenListenersAreInDifferentContexts_ThenEachReceivesOnlyTheChangesOfItsContext()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var (otherGraph, otherWeather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var listener = new RecordingListener();
        var otherListener = new RecordingListener();
        using var registration = dispatcher.Register(graph.Context, listener);
        using var otherRegistration = dispatcher.Register(otherGraph.Context, otherListener);
        registration.PublishWatched(Watch(Gust(weather)));
        otherRegistration.PublishWatched(Watch(Gust(otherWeather)));

        // Act
        weather.Gust = 9;
        graph.Add("Garden", new TestSensor());

        // Assert
        Assert.Equal(2, dispatcher.SourceCount);
        Assert.Single(listener.Changes);
        Assert.Equal(1, listener.GraphChanges);
        Assert.Empty(otherListener.Changes);
        Assert.Equal(0, otherListener.GraphChanges);
    }

    [Fact]
    public async Task WhenListenersComeAndGoWhileOtherThreadsWrite_ThenNothingThrowsAndAStableWatcherReceivesEveryChange()
    {
        // Arrange
        const int writerCount = 4;
        const int writesPerWriter = 500;
        const int churnerCount = 2;
        const int churnRounds = 200;
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var stable = new RecordingListener();
        var stableRegistration = dispatcher.Register(graph.Context, stable);
        stableRegistration.PublishWatched(Watch(Gust(weather)));
        using var start = new Barrier(writerCount + churnerCount);

        // Act
        var writers = Enumerable.Range(0, writerCount).Select(writer => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var index = 0; index < writesPerWriter; index++)
            {
                weather.Gust = writer * writesPerWriter + index + 1;
            }
        }, TaskCreationOptions.LongRunning));
        var churners = Enumerable.Range(0, churnerCount).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var round = 0; round < churnRounds; round++)
            {
                using var registration = dispatcher.Register(graph.Context, new RecordingListener());
                registration.PublishWatched(Watch(Gust(weather)));
                registration.PublishWatched(Watch(Gust(weather), Rain(weather)));
                registration.PublishRoute(Watch(Rain(weather)), isUnbounded: false);
                registration.PublishWatched(FrozenSet<PropertyReference>.Empty);
            }
        }, TaskCreationOptions.LongRunning));
        await Task.WhenAll(writers.Concat(churners));
        var watchersAfterChurn = dispatcher.CountWatchers(graph.Context, Gust(weather));
        stableRegistration.Dispose();

        // Assert
        Assert.Equal(writerCount * writesPerWriter, stable.Changes.Count);
        Assert.Equal(1, watchersAfterChurn);
        Assert.Equal(0, dispatcher.SourceCount);
    }

    private static (TestGraph Graph, TestSensor Weather) CreateGraph()
    {
        var graph = new TestGraph();
        var weather = new TestSensor();
        graph.Add("Weather", weather);
        return (graph, weather);
    }

    private static PropertyReference Gust(TestSensor weather) => new(weather, nameof(TestSensor.Gust));

    private static PropertyReference Rain(TestSensor weather) => new(weather, nameof(TestSensor.Rain));

    private static PropertyReference SensorOf(TestSensorHolder holder) => new(holder, nameof(TestSensorHolder.Sensor));

    private static PropertyReference ChildrenOf(TestGraph graph) => new(graph.Root, nameof(TestRoot.Children));

    private static FrozenSet<PropertyReference> Watch(params PropertyReference[] properties) => properties.ToFrozenSet();

    private sealed class RecordingListener : IAutomationChangeListener
    {
        private readonly ConcurrentQueue<PropertyChangedInput> _changes = new();
        private readonly ConcurrentQueue<int> _graphChainDepths = new();

        public IReadOnlyList<PropertyChangedInput> Changes => [.. _changes];

        public IReadOnlyList<int> GraphChainDepths => [.. _graphChainDepths];

        public int GraphChanges => _graphChainDepths.Count;

        public void OnWatchedPropertyChanged(PropertyChangedInput input) => _changes.Enqueue(input);

        public void OnGraphChanged(PropertyReference property, int chainDepth) => _graphChainDepths.Enqueue(chainDepth);
    }
}
