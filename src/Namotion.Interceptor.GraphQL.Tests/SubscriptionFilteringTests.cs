using System.Reactive.Concurrency;
using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Xunit;

namespace Namotion.Interceptor.GraphQL.Tests;

public class SubscriptionFilteringTests
{
    [Fact]
    public void SelectionMatcher_WhenPropertyMatchesSelection_ReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var sensor = new Sensor(context);
        var selectedPaths = new HashSet<string> { "temperature" };
        var pathProvider = CamelCasePathProvider.Instance;

        // Capture all changes
        var allChanges = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            allChanges.Add(change);
        });

        // Act - trigger a property change
        sensor.Temperature = 42.0m;

        // Assert - find the temperature change (not the derived Status change)
        Assert.NotEmpty(allChanges);
        var temperatureChange = allChanges.FirstOrDefault(c =>
            c.Property.TryGetRegisteredProperty() is { } prop &&
            pathProvider.TryGetPropertySegment(prop) == "temperature");

        // Verify we found a temperature change (default struct means no property was found)
        Assert.NotEqual(default, temperatureChange);

        var matches = GraphQLSelectionMatcher.IsPropertyInSelection(
            temperatureChange, selectedPaths, pathProvider, sensor);
        Assert.True(matches);
    }

    [Fact]
    public void SelectionMatcher_WhenPropertyDoesNotMatchSelection_ReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var sensor = new Sensor(context);
        var selectedPaths = new HashSet<string> { "temperature" }; // Only temperature selected
        var pathProvider = CamelCasePathProvider.Instance;

        // Get a property change for humidity (not selected)
        SubjectPropertyChange? capturedChange = null;
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            capturedChange = change;
        });

        // Act - trigger a property change for humidity
        sensor.Humidity = 80.0m;

        // Assert
        Assert.NotNull(capturedChange);
        var change = capturedChange!.Value;
        var matches = GraphQLSelectionMatcher.IsPropertyInSelection(
            change, selectedPaths, pathProvider, sensor);
        Assert.False(matches);
    }

    [Fact]
    public void SelectionMatcher_WhenNestedPropertyMatchesSelection_ReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var sensor = new Sensor(context);
        sensor.Location = new Location(context);
        var selectedPaths = new HashSet<string> { "location", "location.building" };
        var pathProvider = CamelCasePathProvider.Instance;

        // Get a property change for location.building
        SubjectPropertyChange? capturedChange = null;
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            capturedChange = change;
        });

        // Act - trigger a nested property change
        sensor.Location.Building = "Building A";

        // Assert
        Assert.NotNull(capturedChange);
        var change = capturedChange!.Value;
        var matches = GraphQLSelectionMatcher.IsPropertyInSelection(
            change, selectedPaths, pathProvider, sensor);
        Assert.True(matches);
    }

    [Fact]
    public async Task Subscription_WhenSubscribedPropertyChanges_ReceivesUpdate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var sensor = new Sensor(context);

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>()
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        var result = await executor.ExecuteAsync("subscription { root { temperature } }");
        var stream = ((IResponseStream)result).ReadResultsAsync();
        var enumerator = stream.GetAsyncEnumerator();

        // Start consuming the stream before changing the property,
        // so the Rx subscription pipeline is active when the change fires.
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        // Allow the Rx pipeline to fully subscribe before triggering the change.
        await Task.Delay(100);

        // Act - change subscribed property
        sensor.Temperature = 42.0m;

        // Assert - should receive update
        var hasResult = await moveNextTask.WaitAsync(cancellationTokenSource.Token);
        Assert.True(hasResult);
        var json = JsonSerializer.Serialize(enumerator.Current.Data);
        Assert.Contains("42", json);
    }

    [Fact]
    public async Task Subscription_WhenUnsubscribedPropertyChanges_DoesNotReceiveUpdate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var sensor = new Sensor(context);

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>()
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        var result = await executor.ExecuteAsync("subscription { root { temperature } }");
        var stream = ((IResponseStream)result).ReadResultsAsync();
        var enumerator = stream.GetAsyncEnumerator();

        // Start consuming the stream and allow the Rx pipeline to fully subscribe.
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(100);

        // Act - change UNsubscribed property
        sensor.Humidity = 80.0m;

        // Assert - should NOT receive update within timeout
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var receivedUpdate = false;
        try
        {
            receivedUpdate = await moveNextTask.WaitAsync(cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected - no update received
        }

        Assert.False(receivedUpdate, "Should not receive update for unsubscribed property");
    }
}
