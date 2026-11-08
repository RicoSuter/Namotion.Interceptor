using System.Collections.Concurrent;
using System.Collections.Frozen;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

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
    public void WhenPropertiesAreAddedToAnAttachedSubject_ThenEachListenerIsNotifiedOnceUntilItReleasesTheGraphChange()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var first = new RecordingListener();
        var second = new RecordingListener();
        using var firstRegistration = dispatcher.Register(graph.Context, first);
        using var secondRegistration = dispatcher.Register(graph.Context, second);
        var registered = weather.TryGetRegisteredSubject()!;

        // Act
        registered.AddProperty<double?>("Wind", _ => 10);
        registered.AddProperty<double?>("Pressure", _ => null);
        var graphChangesBeforeTheRelease = first.GraphChanges;
        firstRegistration.OnGraphChangeReleased();
        registered.AddProperty<double?>("Humidity", _ => 50);

        // Assert
        Assert.Equal(1, graphChangesBeforeTheRelease);
        Assert.Equal(2, first.GraphChanges);
        Assert.Equal(2, second.GraphChanges);
    }

    [Fact]
    public void WhenSeveralDispatchersRouteAContext_ThenOneNotifierAnnouncesAnAddedPropertyToTheListenersOfEach()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var first = new RecordingListener();
        var second = new RecordingListener();
        using var firstRegistration = new AutomationChangeDispatcher().Register(graph.Context, first);
        using var secondRegistration = new AutomationChangeDispatcher().Register(graph.Context, second);

        // Act
        weather.TryGetRegisteredSubject()!.AddProperty<double?>("Wind", _ => 10);

        // Assert
        Assert.Single(graph.Context.GetServices<IPropertyLifecycleHandler>().OfType<PropertyAttachNotifier>());
        Assert.Equal(1, first.GraphChanges);
        Assert.Equal(1, second.GraphChanges);
    }

    [Fact]
    public void WhenNoDispatcherIsRegistered_ThenEveryCallerSharesOne()
    {
        // Arrange
        var services = new ServiceCollection().BuildServiceProvider();

        // Act
        var first = AutomationChangeDispatcher.From(services);
        var second = AutomationChangeDispatcher.From(services);

        // Assert
        Assert.Same(first, second);
    }

    [Fact]
    public void WhenALifecycleHandlerFailsTheAttachOfASubject_ThenAPropertyAddedToItAfterAnotherChangeIsAnnounced()
    {
        // Arrange
        var (graph, _) = CreateGraph();
        var holder = new TestSensorHolder();
        graph.Add("Holder", holder);
        var dispatcher = new AutomationChangeDispatcher();
        var listener = new RecordingListener();
        using var registration = dispatcher.Register(graph.Context, listener);
        var garden = new TestSensor();
        graph.Context.AddService<ILifecycleHandler>(new FailingAttachHandler(garden));
        using var failed = new ManualResetEventSlim();
        using var changed = new ManualResetEventSlim();
        var graphChangesBeforeTheAdd = -1;
        Exception? attachError = null;
        var writer = new Thread(() =>
        {
            attachError = Record.Exception(() => graph.Add("Garden", garden));
            failed.Set();
            changed.Wait(TimeSpan.FromSeconds(30));
            registration.OnGraphChangeReleased();
            graphChangesBeforeTheAdd = listener.GraphChanges;

            // Act
            garden.TryGetRegisteredSubject()!.AddProperty<double?>("Wind", _ => 10);
        });

        // Act
        writer.Start();
        failed.Wait(TimeSpan.FromSeconds(30));
        holder.Sensor = new TestSensor();
        changed.Set();
        writer.Join(TimeSpan.FromSeconds(30));

        // Assert
        Assert.IsType<InvalidOperationException>(attachError);
        Assert.Equal(graphChangesBeforeTheAdd + 1, listener.GraphChanges);
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

    // A route has properties that hold no subjects, such as one looked up while it was missing; their changes leave every
    // path as it is.
    [Fact]
    public void WhenAPropertyOnARouteHoldsNoSubjects_ThenItsChangeIsNoGraphChange()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var listener = new RecordingListener();
        using var registration = dispatcher.Register(graph.Context, listener);
        registration.PublishRoute(Watch(Gust(weather)), isUnbounded: false);

        // Act
        weather.Gust = 9;

        // Assert
        Assert.Equal(0, listener.GraphChanges);
        Assert.False(dispatcher.IsRoutedAsGraphChange(graph.Context, listener, Gust(weather)));
    }

    [Fact]
    public void WhenARouteIsUnbounded_ThenAChangeOfEveryPropertyThatHoldsSubjectsIsAGraphChange()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var holder = new TestSensorHolder();
        graph.Add("Holder", holder);
        var dispatcher = new AutomationChangeDispatcher();
        var listener = new RecordingListener();
        using var registration = dispatcher.Register(graph.Context, listener);
        registration.PublishRoute(Watch(Gust(weather)), isUnbounded: true);

        // Act
        weather.Gust = 9;
        holder.Sensor = new TestSensor();
        registration.OnGraphChangeReleased();
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
        graph.Add("Garden", new TestSensor());

        // Assert
        Assert.Equal(0, leaving.GraphChanges);
        Assert.Equal(1, staying.GraphChanges);
        Assert.False(dispatcher.IsRoutedAsGraphChange(graph.Context, leaving, SensorOf(holder)));
        Assert.True(dispatcher.IsRoutedAsGraphChange(graph.Context, staying, SensorOf(holder)));
    }

    // A property added with a null value publishes no change, so its attach is what tells a listener that looked for it.
    [Fact]
    public void WhenAPropertyIsAddedAtRunTime_ThenItIsAGraphChangeOnlyForTheListenersWhoseRouteHasItOrIsUnbounded()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var dispatcher = new AutomationChangeDispatcher();
        var routed = new RecordingListener();
        var unbounded = new RecordingListener();
        var unrouted = new RecordingListener();
        using var routedRegistration = dispatcher.Register(graph.Context, routed);
        using var unboundedRegistration = dispatcher.Register(graph.Context, unbounded);
        using var unroutedRegistration = dispatcher.Register(graph.Context, unrouted);
        routedRegistration.PublishRoute(Watch(new PropertyReference(weather, "Wind")), isUnbounded: false);
        unroutedRegistration.PublishRoute(Watch(Gust(weather)), isUnbounded: false);

        // Act
        ((IInterceptorSubject)weather).TryGetRegisteredSubject()!.AddProperty("Wind", typeof(double?), _ => null, null);

        // Assert
        Assert.Equal(1, routed.GraphChanges);
        Assert.Equal(1, unbounded.GraphChanges);
        Assert.Equal(0, unrouted.GraphChanges);
    }

    [Fact]
    public void WhenEveryRouteIsMadeUnbounded_ThenAPublishedRouteRoutesEveryGraphChange()
    {
        // Arrange
        var (graph, weather) = CreateGraph();
        var holder = new TestSensorHolder();
        graph.Add("Holder", holder);
        var dispatcher = new AutomationChangeDispatcher { MakesEveryRouteUnbounded = true };
        var listener = new RecordingListener();
        using var registration = dispatcher.Register(graph.Context, listener);
        registration.PublishRoute(Watch(SensorOf(holder), new PropertyReference(weather, "Wind")), isUnbounded: false);

        // Act
        ((IInterceptorSubject)weather).TryGetRegisteredSubject()!.AddProperty("Wind", typeof(double?), _ => null, null);
        registration.OnGraphChangeReleased();
        graph.Add("Garden", new TestSensor());

        // Assert
        Assert.Equal(2, listener.GraphChanges);
        Assert.True(dispatcher.IsRoutedAsGraphChange(graph.Context, listener, ChildrenOf(graph)));
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

    [RunsAfter(typeof(PropertyAttachNotifier))]
    private sealed class FailingAttachHandler(IInterceptorSubject subject) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, subject))
            {
                throw new InvalidOperationException("The attach failed.");
            }
        }
    }

    private sealed class RecordingListener : IAutomationChangeListener
    {
        private readonly ConcurrentQueue<PropertyChangedInput> _changes = new();
        private readonly ConcurrentQueue<int> _graphChainDepths = new();

        public IReadOnlyList<PropertyChangedInput> Changes => [.. _changes];

        public IReadOnlyList<int> GraphChainDepths => [.. _graphChainDepths];

        public int GraphChanges => _graphChainDepths.Count;

        public void OnWatchedPropertyChanged(PropertyChangedInput input) => _changes.Enqueue(input);

        public void OnGraphChanged(int chainDepth) => _graphChainDepths.Enqueue(chainDepth);
    }
}
