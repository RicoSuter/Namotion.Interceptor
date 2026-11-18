using System.Collections.Concurrent;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Automation.Runtime;
using HomeBlaze.Automation.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using static HomeBlaze.Automation.Tests.Testing.RuleBuilder;

namespace HomeBlaze.Automation.Tests;

public class AutomationGraphRouteTests
{
    [Fact]
    public async Task WhenAPropertyThatHoldsSubjectsAndIsNotOnTheRouteChanges_ThenTheAutomationNeitherReceivesAGraphChangeNorResolvesAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var holder = new TestSensorHolder();
        host.Graph.Add("Holder", holder);
        var (automation, listener) = await AddCountedAsync(host, "Gusts", Rule("/Weather/Gust > 8"));
        listener.Reset();
        var resolutionsBefore = Routes(automation).ResolutionCount;

        // Act
        holder.Sensor = new TestSensor { Gust = 9 };
        await automation.FlushAsync();
        var (graphChangesOffTheRoute, resolutionsOffTheRoute) = (listener.GraphChanges, Routes(automation).ResolutionCount - resolutionsBefore);
        host.Graph.Add("Weather", new TestSensor { Gust = 9 });
        await automation.FlushAsync();

        // Assert
        Assert.Equal(0, graphChangesOffTheRoute);
        Assert.Equal(0, resolutionsOffTheRoute);
        Assert.False(Registration(automation).IsUnbounded);
        Assert.NotEqual(0, listener.GraphChanges);
        Assert.NotEqual(resolutionsBefore, Routes(automation).ResolutionCount);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAPropertyOnTheRouteChanges_ThenThePathsAreResolvedAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var holder = new TestSensorHolder { Sensor = new TestSensor { Gust = 3 } };
        host.Graph.Add("Holder", holder);
        var (automation, listener) = await AddCountedAsync(host, "Gusts", Rule("/Holder/Sensor/Gust > 8"));
        listener.Reset();

        // Act
        holder.Sensor = new TestSensor { Gust = 9 };
        await automation.FlushAsync();

        // Assert
        Assert.Equal(1, listener.GraphChanges);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheMissingEntryOfAnUnresolvedPathIsAdded_ThenThePathResolves()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Garden/Gust > 8"));
        var unknownStatus = host.Rule("rule").Status;

        // Act
        host.Graph.Add("Garden", new TestSensor { Gust = 9 });
        await host.FlushAsync();

        // Assert
        Assert.Contains("missing /Garden/Gust", unknownStatus);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenAPropertyIsAddedAtRunTime_ThenAPathToItResolves()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Weather/Wind > 8"));

        // Act
        ((IInterceptorSubject)host.Weather).TryGetRegisteredSubject()!.AddProperty("Wind", typeof(double), _ => 9.0, null);
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAPlainPropertyGainsStateMetadataAtRunTime_ThenAPathToItBecomesReadableAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var device = new TestPlainDevice { Level = 9 };
        host.Graph.Add("Device", device);
        var automation = await host.AddAutomationAsync("Levels", Rule("/Device/Level > 5"));
        var closesWhileUnreadable = host.Blinds.CloseCount;

        // Act
        ((IInterceptorSubject)device).TryGetRegisteredProperty(nameof(TestPlainDevice.Level))!
            .AddAttribute(KnownAttributes.State, typeof(StateMetadata), _ => new StateMetadata(), null);
        await automation.FlushAsync();

        // Assert
        Assert.Equal(0, closesWhileUnreadable);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAPropertyGainsASecretConfigurationAtRunTime_ThenAPathToItIsNoLongerReadAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        double? windValue = 1;
        var wind = ((IInterceptorSubject)host.Weather).TryGetRegisteredSubject()!
            .AddProperty("Wind", typeof(double?), _ => windValue, (_, value) => windValue = (double?)value);
        var automation = await host.AddAutomationAsync("Winds", Rule("/Weather/Wind > 5"));

        // Act
        wind.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata), _ => new ConfigurationMetadata { IsSecret = true }, null);
        await automation.FlushAsync();
        var isWatchedWhileSecret = Registration(automation).Watched.Contains(wind.Reference);
        wind.SetValue(9.0);
        await automation.FlushAsync();

        // Assert
        Assert.False(isWatchedWhileSecret);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The route records the configuration of Gust on the holder of the sensor, which stays the same when the sensor is
    // replaced.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheSubjectAPathEndsAtIsReplacedAndGainsASecretConfiguration_ThenThePathIsNoLongerReadAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var holder = new TestSensorHolder { Sensor = new TestSensor { Gust = 1 } };
        host.Graph.Add("Holder", holder);
        var automation = await host.AddAutomationAsync("Gusts", Rule("/Holder/Sensor/Gust > 5"));
        var sensor = new TestSensor { Gust = 1 };
        holder.Sensor = sensor;
        await automation.FlushAsync();

        // Act
        var gust = ((IInterceptorSubject)sensor).TryGetRegisteredProperty(nameof(TestSensor.Gust))!;
        gust.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata), _ => new ConfigurationMetadata { IsSecret = true }, null);
        await automation.FlushAsync();
        var isWatchedWhileSecret = Registration(automation).Watched.Contains(gust.Reference);
        sensor.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.False(isWatchedWhileSecret);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // An absolute path reaches the root through none of its parents, so the route records the configuration on the root.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAPropertyOfTheRootGainsASecretConfigurationAtRunTime_ThenAPathToItIsNoLongerReadAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        double? windValue = 1;
        var wind = ((IInterceptorSubject)host.Graph.Root).TryGetRegisteredSubject()!
            .AddProperty("Wind", typeof(double?), _ => windValue, (_, value) => windValue = (double?)value);
        var automation = await host.AddAutomationAsync("Winds", Rule("/Wind > 5"));

        // Act
        wind.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata), _ => new ConfigurationMetadata { IsSecret = true }, null);
        await automation.FlushAsync();
        var isWatchedWhileSecret = Registration(automation).Watched.Contains(wind.Reference);
        wind.SetValue(9.0);
        await automation.FlushAsync();

        // Assert
        Assert.False(isWatchedWhileSecret);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The holder of the sensor that the route has stands for the configuration of Gust only while the sensor keeps it as
    // a parent: the plain property is no parent of the sensor, so the sensor loses its holder by the detach of an
    // ancestor of the holder after it gained another one, both off the route.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAPathLeadsThroughAPlainPropertyAndItsSubjectChangesItsParentsOffTheRoute_ThenAnAddedSecretConfigurationHidesItAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 1 };
        var building = new TestFolder();
        var floor = new TestFolder();
        var first = new TestSensorHolder { Sensor = sensor };
        var second = new TestSensorHolder();
        host.Graph.Add("Building", building);
        building.Add("Floor", floor);
        floor.Add("First", first);
        building.Add("Second", second);
        host.Graph.Add("View", new TestSensorView { Plain = sensor });
        var automation = await host.AddAutomationAsync("Gusts", Rule("/View/Plain/Gust > 5"));
        second.Sensor = sensor;
        building.Remove("Floor");
        await automation.FlushAsync();

        // Act
        ((IInterceptorSubject)sensor).TryGetRegisteredProperty(nameof(TestSensor.Gust))!
            .AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata), _ => new ConfigurationMetadata { IsSecret = true }, null);
        await automation.FlushAsync();
        var status = automation.Rules["rule"].Status;
        sensor.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing", status);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The derived property is no parent of the sensor and keeps its value when its source is replaced by a holder of the
    // same sensor, so the sensor loses the holder that it had when the path was resolved without a change on the route.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAPathLeadsThroughADerivedPropertyWhoseSourceIsReplacedByAHolderOfTheSameSubject_ThenAnAddedSecretConfigurationHidesItAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 1 };
        var view = new TestSensorView { Source = new TestSensorHolder { Sensor = sensor } };
        var building = new TestFolder();
        host.Graph.Add("Building", building);
        host.Graph.Add("View", view);
        var automation = await host.AddAutomationAsync("Gusts", Rule("/View/Current/Gust > 5"));
        var second = new TestSensorHolder { Sensor = sensor };
        building.Add("Second", second);
        view.Source = second;
        await automation.FlushAsync();

        // Act
        ((IInterceptorSubject)sensor).TryGetRegisteredProperty(nameof(TestSensor.Gust))!
            .AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata), _ => new ConfigurationMetadata { IsSecret = true }, null);
        await automation.FlushAsync();
        var status = automation.Rules["rule"].Status;
        sensor.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing", status);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The plain property is no parent of the sensor, whose only parent is detached and another one attached, both off
    // the route.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenASubjectReachedThroughAPlainPropertyIsDetachedAndAttachedAgainOffTheRoute_ThenThePathFollowsItsAttachmentAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 9 };
        var building = new TestFolder();
        var floor = new TestFolder();
        host.Graph.Add("Building", building);
        building.Add("Floor", floor);
        floor.Add("First", new TestSensorHolder { Sensor = sensor });
        host.Graph.Add("View", new TestSensorView { Plain = sensor });
        var automation = await host.AddAutomationAsync("Gusts", Rule("/View/Plain/Gust > 5"));

        // Act
        building.Remove("Floor");
        await automation.FlushAsync();
        var detachedStatus = automation.Rules["rule"].Status;
        building.Add("Annex", new TestSensorHolder { Sensor = sensor });
        await automation.FlushAsync();
        var attachedStatus = automation.Rules["rule"].Status;
        sensor.Gust = 1;
        await automation.FlushAsync();
        sensor.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing /View/Plain/Gust", detachedStatus);
        Assert.DoesNotContain("missing", attachedStatus);
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    // The derived property reads its sensor through a plain property, so neither of them is a parent of the sensor.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenASubjectReachedThroughADerivedPropertyOverAPlainOneIsDetachedAndAttachedAgainOffTheRoute_ThenThePathFollowsItsAttachmentAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 9 };
        var building = new TestFolder();
        var floor = new TestFolder();
        var first = new TestSensorHolder { Sensor = sensor };
        host.Graph.Add("Building", building);
        building.Add("Floor", floor);
        floor.Add("First", first);
        host.Graph.Add("View", new TestSensorView { Lookup = first });
        var automation = await host.AddAutomationAsync("Gusts", Rule("/View/ViaLookup/Gust > 5"));

        // Act
        building.Remove("Floor");
        await automation.FlushAsync();
        var detachedStatus = automation.Rules["rule"].Status;
        building.Add("Annex", new TestSensorHolder { Sensor = sensor });
        await automation.FlushAsync();
        var attachedStatus = automation.Rules["rule"].Status;
        sensor.Gust = 1;
        await automation.FlushAsync();
        sensor.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing /View/ViaLookup/Gust", detachedStatus);
        Assert.DoesNotContain("missing", attachedStatus);
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    // The source of the derived property is a parent of the sensor and the view holds it, so the sensor stays attached.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheSourceOfADerivedPropertyIsDetachedOffTheRouteAndReplacedByAHolderOfTheSameSubject_ThenThePathStaysResolvedAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 9 };
        var building = new TestFolder();
        var floor = new TestFolder();
        var first = new TestSensorHolder { Sensor = sensor };
        var view = new TestSensorView { Source = first };
        host.Graph.Add("Building", building);
        building.Add("Floor", floor);
        floor.Add("First", first);
        host.Graph.Add("View", view);
        var automation = await host.AddAutomationAsync("Gusts", Rule("/View/Current/Gust > 5"));

        // Act
        building.Remove("Floor");
        await automation.FlushAsync();
        var detachedStatus = automation.Rules["rule"].Status;
        var second = new TestSensorHolder { Sensor = sensor };
        host.Graph.Add("Annex", second);
        view.Source = second;
        await automation.FlushAsync();
        first.Sensor = null;
        await automation.FlushAsync();

        // Assert
        Assert.DoesNotContain("missing", detachedStatus);
        Assert.DoesNotContain("missing", automation.Rules["rule"].Status);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // The plain getter returns the sensor of the holder, whose replacement writes no property that the route has.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAPlainGetterOnThePathReturnsAnotherSubjectAfterAWriteOffTheRoute_ThenThePathFollowsItAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var holder = new TestSensorHolder { Sensor = new TestSensor { Gust = 1 } };
        host.Graph.Add("View", new TestSensorView { Source = holder });
        var automation = await host.AddAutomationAsync("Gusts", Rule("/View/Pick/Gust > 5"));
        var closesBefore = host.Blinds.CloseCount;

        // Act
        holder.Sensor = new TestSensor { Gust = 9 };
        await automation.FlushAsync();

        // Assert
        Assert.Equal(0, closesBefore);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // The holder reached through the plain property is detached after the sensor gained another parent, both off the
    // route, which leaves the path through the detached holder unresolved.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAHolderReachedThroughAPlainPropertyIsDetachedOffTheRoute_ThenThePathIsMissingAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 1 };
        var building = new TestFolder();
        var floor = new TestFolder();
        var first = new TestSensorHolder { Sensor = sensor };
        host.Graph.Add("Building", building);
        building.Add("Floor", floor);
        floor.Add("First", first);
        host.Graph.Add("View", new TestSensorView { Lookup = first });
        var automation = await host.AddAutomationAsync("Gusts", Rule("/View/Lookup/Sensor/Gust > 5"));
        building.Add("Annex", new TestSensorHolder { Sensor = sensor });
        await automation.FlushAsync();

        // Act
        building.Remove("Floor");
        await automation.FlushAsync();
        var detachedStatus = automation.Rules["rule"].Status;
        sensor.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing /View/Lookup/Sensor/Gust", detachedStatus);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The sensor gains a parent and loses another one off the route before its secret configuration is registered under
    // a name of its own.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenASubjectWithSeveralParentsGainsASecretConfigurationUnderAnotherName_ThenThePathIsNoLongerReadAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 1 };
        var annex = new TestSensorHolder { Sensor = sensor };
        var building = new TestFolder();
        host.Graph.Add("Building", building);
        host.Graph.Add("Main", new TestSensorHolder { Sensor = sensor });
        building.Add("Annex", annex);
        var automation = await host.AddAutomationAsync("Gusts", Rule("/Main/Sensor/Gust > 5"));
        building.Add("Later", new TestSensorHolder { Sensor = sensor });
        await automation.FlushAsync();
        annex.Sensor = null;
        await automation.FlushAsync();

        // Act
        ((IInterceptorSubject)sensor).TryGetRegisteredSubject()!.AddProperty(
            "GustConfiguration", typeof(ConfigurationMetadata), _ => new ConfigurationMetadata { IsSecret = true }, null,
            new PropertyAttributeAttribute(nameof(TestSensor.Gust), KnownAttributes.Configuration));
        await automation.FlushAsync();
        var status = automation.Rules["rule"].Status;
        sensor.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing /Main/Sensor/Gust", status);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The sensor changes while it is detached, so only a resolution at its attach off the route reads the new value.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenASubjectReachedThroughAPlainPropertyChangesWhileDetachedAndIsAttachedAgainOffTheRoute_ThenThePathReadsTheNewValueAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 1 };
        var building = new TestFolder();
        var floor = new TestFolder();
        host.Graph.Add("Building", building);
        building.Add("Floor", floor);
        floor.Add("First", new TestSensorHolder { Sensor = sensor });
        host.Graph.Add("View", new TestSensorView { Plain = sensor });
        var automation = await host.AddAutomationAsync("Gusts", Rule("/View/Plain/Gust > 5"));

        // Act
        building.Remove("Floor");
        await automation.FlushAsync();
        sensor.Gust = 9;
        await automation.FlushAsync();
        var detachedStatus = automation.Rules["rule"].Status;
        var closesWhileDetached = host.Blinds.CloseCount;
        building.Add("Annex", new TestSensorHolder { Sensor = sensor });
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing /View/Plain/Gust", detachedStatus);
        Assert.Equal(0, closesWhileDetached);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // An absolute path reaches the root through none of its parents, which it gains and loses off the route.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheRootLosesItsParentAfterItGainedAnother_ThenAnAddedSecretConfigurationHidesAPathToItAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        double? windValue = 1;
        var wind = ((IInterceptorSubject)host.Graph.Root).TryGetRegisteredSubject()!
            .AddProperty("Wind", typeof(double?), _ => windValue, (_, value) => windValue = (double?)value);
        var building = new TestFolder();
        host.Graph.Add("Building", building);
        building.Add("First", new TestRootHolder { Root = host.Graph.Root });
        var automation = await host.AddAutomationAsync("Winds", Rule("/Wind > 5"));
        building.Add("Second", new TestRootHolder { Root = host.Graph.Root });
        building.Remove("First");
        await automation.FlushAsync();

        // Act
        wind.AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata), _ => new ConfigurationMetadata { IsSecret = true }, null);
        await automation.FlushAsync();
        var status = automation.Rules["rule"].Status;
        wind.SetValue(9.0);
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing", status);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The path climbs to the folder, whose parents it reaches through none of its steps, and the folder gains another
    // parent and loses the first one by the detach of its ancestor.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheFolderAboveTheAutomationChangesItsParentsOffTheRoute_ThenAnAddedSecretConfigurationHidesAPathToItAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var building = new TestFolder();
        var annex = new TestFolder();
        var floor = new TestFolder { Level = 1 };
        host.Graph.Add("Building", building);
        host.Graph.Add("Annex", annex);
        building.Add("Floor", floor);
        var automation = await host.AddAutomationAsync("Levels", Rule("../../Level > 5"), automation => floor.Add("Levels", automation));
        annex.Add("Floor", floor);
        host.Graph.Remove("Building");
        await automation.FlushAsync();

        // Act
        ((IInterceptorSubject)floor).TryGetRegisteredProperty(nameof(TestFolder.Level))!
            .AddAttribute(KnownAttributes.Configuration, typeof(ConfigurationMetadata), _ => new ConfigurationMetadata { IsSecret = true }, null);
        await automation.FlushAsync();
        var status = automation.Rules["rule"].Status;
        floor.Level = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains("missing", status);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The route records the configuration of Gust on the room, which holds the sibling too, under the name of that attribute.
    [Fact]
    public async Task WhenASiblingOfTheSubjectAPathEndsAtGainsPropertiesWithStateMetadata_ThenTheAutomationNeitherReceivesAGraphChangeNorResolvesAgain()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var sibling = new TestSensor();
        host.Graph.Add("Room", new TestRoom { Items = [new TestSensor(), sibling] });
        var (automation, listener) = await AddCountedAsync(host, "Gusts", Rule("/Room/Items[0]/Gust > 5"));
        listener.Reset();
        var resolutionsBefore = Routes(automation).ResolutionCount;
        var registered = ((IInterceptorSubject)sibling).TryGetRegisteredSubject()!;

        // Act
        for (var index = 0; index < 10; index++)
        {
            double? value = null;
            registered.AddProperty($"Reading{index}", typeof(double?), _ => value, (_, newValue) => value = (double?)newValue, new StateAttribute());
            await automation.FlushAsync();
        }

        // Assert
        Assert.Equal(0, listener.GraphChanges);
        Assert.Equal(resolutionsBefore, Routes(automation).ResolutionCount);
    }

    [Fact]
    public async Task WhenAWatchedPropertyChanges_ThenNoPathIsResolved()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Weather/Gust > 8"));
        var resolutionsBefore = Routes(host.Automation).ResolutionCount;

        // Act
        host.Weather.Gust = 9;
        await host.FlushAsync();
        host.Weather.Gust = 0;
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(resolutionsBefore, Routes(host.Automation).ResolutionCount);
    }

    // A property added with a null value publishes no change, so the attach of the property is what routes a graph change.
    [Fact]
    public async Task WhenAPropertyIsAddedAtRunTimeWithANullValue_ThenAPathToItResolvesAndTheValueIsWatched()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(Rule("/Weather/Wind > 8"));
        double? wind = null;
        var registered = ((IInterceptorSubject)host.Weather).TryGetRegisteredSubject()!;

        // Act
        var property = registered.AddProperty("Wind", typeof(double?), _ => wind, (_, value) => wind = (double?)value);
        await host.FlushAsync();
        var isWatchedAfterAdding = Registration(host.Automation).Watched.Contains(property.Reference);
        property.SetValue(9.0);
        await host.FlushAsync();

        // Assert
        Assert.True(isWatchedAfterAdding);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheParentOfTheAutomationIsReplacedAtTheSamePath_ThenARelativePathFollowsIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var calm = new TestFolder { Level = 1 };
        host.Graph.Add("Room", calm);
        var automation = await host.AddAutomationAsync("Levels", Rule("../../Level > 5"), automation => calm.Add("Levels", automation));
        var stormy = new TestFolder { Level = 9, Children = new Dictionary<string, IInterceptorSubject> { ["Levels"] = automation } };
        var pathBefore = automation.Runtime!.Location.AutomationPath;

        // Act
        host.Graph.Add("Room", stormy);
        await automation.FlushAsync();

        // Assert
        Assert.Equal("/Room/Levels", pathBefore);
        Assert.Equal("/Room/Levels", automation.Runtime.Location.AutomationPath);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheAutomationGainsASecondParent_ThenARelativePathThroughItBecomesAmbiguous()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestFolder { Level = 9 };
        var other = new TestFolder();
        host.Graph.Add("Room", room);
        host.Graph.Add("Other", other);
        var automation = await host.AddAutomationAsync("Levels", Rule("../../Level > 5"), automation => room.Add("Levels", automation));
        var statusBefore = automation.Rules["rule"].Status;

        // Act
        other.Add("Levels", automation);
        await automation.FlushAsync();

        // Assert
        Assert.DoesNotContain("missing", statusBefore);
        Assert.Contains("missing ../../Level", automation.Rules["rule"].Status);
    }

    [Fact]
    public async Task WhenARuleGainsASecondParent_ThenARelativePathFromItBecomesAmbiguous()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("rule", On("Rule", When("../Enabled == true"), Invoke("/Blinds/Close")))));
        var statusBefore = host.Rule("rule").Status;
        var otherHost = new TestRuleHost();
        host.Graph.Add("OtherHost", otherHost);
        await host.FlushAsync();

        // Act
        otherHost.Rules = new Dictionary<string, AutomationRule> { ["copy"] = host.Rule("rule") };
        await host.FlushAsync();

        // Assert
        Assert.DoesNotContain("missing", statusBefore);
        Assert.Contains("missing ../Enabled", host.Rule("rule").Status);
    }

    // A relative path starts at the rule, which a write of the Rules property detaches; the rule changes while no change
    // of it is published.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenARuleChangesWhileDetachedAndIsAttachedAgain_ThenAPathFromItResolvesAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var automation = await host.AddAutomationAsync("Self", automation => automation.Rules = Keyed(
            ("rule", On("Gusty", When("./Name == \"Calm\""), Invoke("/Blinds/Close")))));
        var rule = automation.Rules["rule"];
        var closesBefore = host.Blinds.CloseCount;

        // Act
        automation.Rules = new Dictionary<string, AutomationRule>();
        await automation.FlushAsync();
        rule.Name = "Calm";
        automation.Rules = new Dictionary<string, AutomationRule> { ["rule"] = rule };
        await automation.FlushAsync();

        // Assert
        Assert.Equal(0, closesBefore);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenARuleIsRemovedAndAddedAgainWithoutApplying_ThenAPathFromItIsWatchedOnlyWhileAttachedAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var automation = await host.AddAutomationAsync("Self", automation => automation.Rules = Keyed(
            ("rule", On("Gusty", When("./Name == \"Gusty\""), Invoke("/Blinds/Close")))));
        var rule = automation.Rules["rule"];
        var name = new PropertyReference(rule, nameof(AutomationRule.Name));
        var closesBefore = host.Blinds.CloseCount;

        // Act
        automation.Rules = new Dictionary<string, AutomationRule>();
        await automation.FlushAsync();
        var isWatchedWhileRemoved = Registration(automation).Watched.Contains(name);
        automation.Rules = new Dictionary<string, AutomationRule> { ["rule"] = rule };
        await automation.FlushAsync();
        rule.Name = "Calm";
        await automation.FlushAsync();
        rule.Name = "Gusty";
        await automation.FlushAsync();

        // Assert
        Assert.Equal(1, closesBefore);
        Assert.False(isWatchedWhileRemoved);
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    // State metadata registered under a name of its own is still found by the property it belongs to.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAPlainPropertyGainsStateMetadataUnderAnotherName_ThenAPathToItBecomesReadableAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var device = new TestPlainDevice { Level = 9 };
        host.Graph.Add("Device", device);
        var automation = await host.AddAutomationAsync("Levels", Rule("/Device/Level > 5"));
        var closesWhileUnreadable = host.Blinds.CloseCount;

        // Act
        ((IInterceptorSubject)device).TryGetRegisteredSubject()!.AddProperty(
            "LevelState", typeof(StateMetadata), _ => new StateMetadata(), null, new PropertyAttributeAttribute(nameof(TestPlainDevice.Level), KnownAttributes.State));
        await automation.FlushAsync();

        // Assert
        Assert.Equal(0, closesWhileUnreadable);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // No path starts at a rule, so the Rules property of the automation is on no route.
    [Fact]
    public async Task WhenANewRuleGainsItsFirstParentInTheAutomation_ThenTheAutomationReceivesAGraphChange()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var (automation, listener) = await AddCountedAsync(host, "Gusts", Rule("/Weather/Gust > 8"));
        var rulesOfAutomation = new PropertyReference(automation, nameof(Automation.Rules));
        var isRulesRouted = Registration(automation).Route.Contains(rulesOfAutomation);
        listener.Reset();

        // Act
        automation.Rules = new Dictionary<string, AutomationRule>(automation.Rules)
        {
            ["added"] = On("Added", When("/Weather/Rain > 5"), Invoke("/Blinds/Open"))
        };
        await automation.FlushAsync();

        // Assert
        Assert.False(isRulesRouted);
        Assert.False(Registration(automation).IsUnbounded);
        Assert.Equal(1, listener.GraphChanges);
    }

    // The write that gives the rule a second parent also changes a watched property; without routes the watched change
    // of a write is routed before its graph change.
    [Fact]
    public async Task WhenAWriteChangesAWatchedPropertyAndGivesARuleASecondParent_ThenTheWatchedChangeComesBeforeTheGraphChange()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var otherHost = new TestRuleHost();
        host.Graph.Add("OtherHost", otherHost);
        var (automation, listener) = await AddCountedAsync(host, "Rules", automation => automation.Rules = Keyed(
            ("rule", On("Rule", When("/OtherHost/Rules != null and ../Enabled == true"), Invoke("/Blinds/Close")))));
        var rulesOfOtherHost = new PropertyReference(otherHost, nameof(TestRuleHost.Rules));
        var isWatched = Registration(automation).Watched.Contains(rulesOfOtherHost);
        var isRouted = Registration(automation).Route.Contains(rulesOfOtherHost);
        listener.Reset();

        // Act
        otherHost.Rules = new Dictionary<string, AutomationRule> { ["copy"] = automation.Rules["rule"] };
        await automation.FlushAsync();

        // Assert
        Assert.True(isWatched);
        Assert.False(isRouted);
        Assert.Equal(["watched", "graph"], listener.Calls);
        Assert.Contains("missing ../Enabled", automation.Rules["rule"].Status);
    }

    [Fact]
    public async Task WhenAnEntryOfADictionaryOrACollectionChanges_ThenThePathsFollowIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("keyed", On("Keyed", When("/Room/Devices[gauge]/Gust > 8"), Invoke("/Blinds/Close"))),
            ("indexed", On("Indexed", When("/Room/Items[1]/Rain > 2"), Invoke("/Blinds/Open")))));
        var room = new TestRoom { Items = [new TestSensor { Rain = 0 }, new TestSensor { Rain = 1 }] };
        host.Graph.Add("Room", room);
        await host.FlushAsync();

        // Act
        room.Devices = new Dictionary<string, TestSensor> { ["gauge"] = new() { Gust = 9 } };
        room.Items = [new TestSensor { Rain = 0 }, new TestSensor { Rain = 5 }, room.Items[1]];
        await host.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.Equal(1, host.Blinds.OpenCount);
    }

    [Fact]
    public async Task WhenADictionaryGainsASecondKeyForASubjectItHolds_ThenThePathThroughTheNewKeyResolves()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var sensor = new TestSensor { Gust = 9 };
        var room = new TestRoom { Devices = new Dictionary<string, TestSensor> { ["d1"] = sensor } };
        host.Graph.Add("Room", room);
        var automation = await host.AddAutomationAsync("Gusts", Rule("/Room/Devices[d2]/Gust > 5"));

        // Act
        room.Devices = new Dictionary<string, TestSensor> { ["d1"] = sensor, ["d2"] = sensor };
        await automation.FlushAsync();

        // Assert
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.False(Routes(automation).IsUnbounded);
    }

    // The other automation resolves the shared absolute path after a change on its own route, so that the resolver holds
    // the result in its cache when the dictionary gains the second key.
    [Fact]
    public async Task WhenAnotherAutomationCachedAPathBeforeADictionaryGainsASecondKeyForASubjectItHolds_ThenBothResolveThePathThroughTheNewKey()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var sensor = new TestSensor { Gust = 9 };
        var room = new TestRoom { Devices = new Dictionary<string, TestSensor> { ["d1"] = sensor } };
        var other = new TestSensorHolder { Sensor = new TestSensor() };
        host.Graph.Add("Room", room);
        host.Graph.Add("Other", other);
        var first = await host.AddAutomationAsync("First", Rule("/Room/Devices[d2]/Gust > 5"));
        var second = await host.AddAutomationAsync("Second", automation => automation.Rules = Keyed(
            ("rule", On("Rule", When("/Room/Devices[d2]/Gust > 5"), Invoke("/Blinds/Close"))),
            ("other", On("Other", When("/Other/Sensor/Gust > 5"), Invoke("/Blinds/Open")))));
        other.Sensor = new TestSensor();
        await second.FlushAsync();

        // Act
        room.Devices = new Dictionary<string, TestSensor> { ["d1"] = sensor, ["d2"] = sensor };
        await first.FlushAsync();
        await second.FlushAsync();

        // Assert
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    // A subject attached to the context directly moves the structure version without a write, and the state change of
    // the other automation caches the absolute path at that version before the dictionary gains the second key.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenTheStructureVersionMovedWithoutAWriteBeforeADictionaryGainsASecondKey_ThenThePathThroughTheNewKeyResolvesAsWithoutGraphRoutes(bool makesEveryRouteUnbounded)
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { }, makesEveryRouteUnbounded: makesEveryRouteUnbounded);
        var sensor = new TestSensor { Gust = 1 };
        var room = new TestRoom { Devices = new Dictionary<string, TestSensor> { ["d0"] = sensor } };
        host.Graph.Add("R0", room);
        var gusts = await host.AddAutomationAsync("A", Rule("/R0/Devices[d1]/Gust > 5"));
        var other = await host.AddAutomationAsync("B", automation =>
        {
            automation.States = ["Day", "Night"];
            automation.InitialState = "Day";
            automation.Rules = Keyed(
                ("toNight", Create("To night", AutomationRuleMode.On, [When("/Weather/Rain > 5")], [Enter("Night")], inState: "Day")),
                ("night", Create("Night", AutomationRuleMode.DryRun, [When("/R0/Devices[d1]/Gust > 5")], [Invoke("/Blinds/Open")], inState: "Night")));
        });

        // Act
        _ = new TestSensorHolder(host.Graph.Context);
        host.Weather.Rain = 9;
        await other.FlushAsync();
        await other.FlushAsync();
        room.Devices = new Dictionary<string, TestSensor> { ["d0"] = sensor, ["d1"] = sensor };
        await gusts.FlushAsync();
        sensor.Gust = 9;
        await gusts.FlushAsync();

        // Assert
        Assert.Equal("Night", other.CurrentState);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenTheSourceOfAnEventTriggerIsAddedLater_ThenTheTriggerSubscribesToIt()
    {
        // Arrange
        var room = new TestRoom();
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/Room/Button", "TestPressEvent")], [Invoke("/Blinds/Close")]))));
        host.Graph.Add("Room", room);
        await host.FlushAsync();
        var statusBefore = host.Rule("press").Status;
        var button = new TestButton();

        // Act
        room.Button = button;
        await host.FlushAsync();
        button.Press("Release");
        await host.FlushAsync();

        // Assert
        Assert.Equal("On: No subject at /Room/Button", statusBefore);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // The write runs after the paths were resolved and before the union that has the new sensor holder is published, so
    // that the union before, which does not have that holder, does not route it.
    [Fact]
    public async Task WhenAPropertyOnlyTheNewRouteHasChangesBeforeTheRouteIsPublished_ThenItIsAGraphChangeAtTheChainDepthOfTheWriter()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        host.Graph.Add("Room", new TestRoom { Sensor = new TestSensor { Gust = 3 } });
        var (automation, listener) = await AddCountedAsync(host, "Gusts", Rule("/Room/Sensor/Gust > 8"));
        var replacement = new TestRoom { Sensor = new TestSensor { Gust = 4 } };
        var windy = new TestSensor { Gust = 9 };
        var routes = Routes(automation);
        var sensorOfReplacement = new PropertyReference(replacement, nameof(TestRoom.Sensor));
        var wasRoutedAtTheWrite = true;
        routes.BeforePublication = () =>
        {
            routes.BeforePublication = null;
            wasRoutedAtTheWrite = Registration(automation).Route.Contains(sensorOfReplacement);
            var writer = new Thread(() =>
            {
                using var run = AutomationInputQueue.EnterRun(5);
                replacement.Sensor = windy;
            });
            writer.Start();
            writer.Join();
        };
        listener.Reset();

        // Act
        host.Graph.Add("Room", replacement);
        await automation.FlushAsync();

        // Assert
        Assert.False(wasRoutedAtTheWrite);
        Assert.Equal([0, 6], listener.ChainDepths);
        var trigger = automation.Runtime!.Engine.Rules[0].PropertyTriggers[0];
        Assert.Equal([new PropertyReference(windy, nameof(TestSensor.Gust))], trigger.Dependencies);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    // A Save hands the trigger to the new rule instance, which its relative path starts at now; the trigger is evaluated
    // at the next graph change, whatever it changes, so the route is unbounded until then.
    [Fact]
    public async Task WhenASaveLeadsATriggerElsewhere_ThenTheNextGraphChangeOffItsRouteEvaluatesIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(automation => automation.Rules = Keyed(
            ("rule", On("Calm", When("./Name == Gusty"), Invoke("/Blinds/Close")))));
        var holder = new TestSensorHolder();
        host.Graph.Add("Holder", holder);
        await host.FlushAsync();
        host.Automation.Rules = Keyed(("rule", On("Gusty", When("./Name == Gusty"), Invoke("/Blinds/Close"))));
        await host.FlushAsync();
        await host.Automation.ApplyConfigurationAsync(CancellationToken.None);
        await host.FlushAsync();
        var closesAfterTheSave = host.Blinds.CloseCount;

        // Act
        holder.Sensor = new TestSensor();
        await host.FlushAsync();

        // Assert
        Assert.Equal(0, closesAfterTheSave);
        Assert.Equal(1, host.Blinds.CloseCount);
        Assert.False(Routes(host.Automation).IsUnbounded);
    }

    [Fact]
    public async Task WhenTheRulesAreDeactivated_ThenTheirRoutesLeaveThePublishedRoute()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var holder = new TestSensorHolder { Sensor = new TestSensor() };
        host.Graph.Add("Holder", holder);
        var (automation, listener) = await AddCountedAsync(host, "Gusts", automation => automation.Rules = Keyed(
            ("gusts", On("Gusts", When("/Holder/Sensor/Gust > 8"), Invoke("/Blinds/Close"))),
            ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/Holder/Sensor", "TestPressEvent")], [Invoke("/Blinds/Open")]))));
        var sensorOfHolder = new PropertyReference(holder, nameof(TestSensorHolder.Sensor));
        var wasRouted = Registration(automation).Route.Contains(sensorOfHolder);

        // Act
        automation.Enabled = false;
        await automation.ApplyConfigurationAsync(CancellationToken.None);
        await automation.FlushAsync();
        listener.Reset();
        holder.Sensor = new TestSensor();
        await automation.FlushAsync();

        // Assert
        Assert.True(wasRouted);
        Assert.DoesNotContain(sensorOfHolder, (IReadOnlySet<PropertyReference>)Registration(automation).Route);
        Assert.DoesNotContain(sensorOfHolder, Routes(automation).Union);
        Assert.Equal(0, listener.GraphChanges);
    }

    // No other rule reads the sensor of the room, so only the rearm expression puts its holder on the route.
    [Fact]
    public async Task WhenTheSubjectThatARearmExpressionReadsIsReplaced_ThenTheTriggerRearmsOnIt()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestRoom { Sensor = new TestSensor { Rain = 5 } };
        host.Graph.Add("Room", room);
        var automation = await host.AddAutomationAsync("Rearm", automation => automation.Rules = Keyed(
            ("rule", On("Rule", When("/Weather/Gust > 8", rearm: "/Room/Sensor/Rain < 3"), Invoke("/Blinds/Close")))));
        host.Weather.Gust = 9;
        await automation.FlushAsync();
        host.Weather.Gust = 0;
        await automation.FlushAsync();

        // Act
        room.Sensor = new TestSensor { Rain = 1 };
        await automation.FlushAsync();
        host.Weather.Gust = 9;
        await automation.FlushAsync();

        // Assert
        Assert.Contains(new PropertyReference(room, nameof(TestRoom.Sensor)), Routes(automation).Union);
        Assert.Equal(2, host.Blinds.CloseCount);
    }

    [Fact]
    public async Task WhenATriggerStopsWaiting_ThenTheRouteOfItsOnlyIfLeavesTheUnion()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestRoom { Sensor = new TestSensor { Power = 0 } };
        host.Graph.Add("Room", room);
        var automation = await host.AddAutomationAsync("Guarded", automation => automation.Rules = Keyed(
            ("rule", Create("Guarded", AutomationRuleMode.On, [When("/Weather/Gust > 8")], [Invoke("/Blinds/Close")], onlyIf: "/Room/Sensor/Power > 5"))));
        var sensorOfRoom = new PropertyReference(room, nameof(TestRoom.Sensor));
        host.Weather.Gust = 9;
        await automation.FlushAsync();
        var isRoutedWhileWaiting = Routes(automation).Union.Contains(sensorOfRoom);

        // Act
        host.Weather.Gust = 0;
        await automation.FlushAsync();

        // Assert
        Assert.True(isRoutedWhileWaiting);
        Assert.DoesNotContain(sensorOfRoom, Routes(automation).Union);
        Assert.DoesNotContain(sensorOfRoom, (IReadOnlySet<PropertyReference>)Registration(automation).Route);
        Assert.Equal(0, host.Blinds.CloseCount);
    }

    // The run enters a state, whose activation drops the trigger that the run consumed before the run is tracked.
    [Fact]
    public async Task WhenARunThatEntersAStateEndsTheWaitOfItsTrigger_ThenTheRouteOfItsOnlyIfLeavesTheUnion()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestRoom { Sensor = new TestSensor { Power = 0 } };
        host.Graph.Add("Room", room);
        var automation = await host.AddAutomationAsync("Guarded", automation =>
        {
            automation.States = ["Day", "Night"];
            automation.InitialState = "Day";
            automation.Rules = Keyed(
                ("rule", Create("Guarded", AutomationRuleMode.On, [When("/Weather/Gust > 8")], [Enter("Night")], onlyIf: "/Room/Sensor/Power > 5")));
        });
        var sensorOfRoom = new PropertyReference(room, nameof(TestRoom.Sensor));
        host.Weather.Gust = 9;
        await automation.FlushAsync();
        var isRoutedWhileWaiting = Routes(automation).Union.Contains(sensorOfRoom);

        // Act
        room.Sensor.Power = 9;
        await automation.FlushAsync();

        // Assert
        Assert.True(isRoutedWhileWaiting);
        Assert.Equal("Night", automation.CurrentState);
        Assert.DoesNotContain(sensorOfRoom, Routes(automation).Union);
    }

    // The Save hands the state of each trigger to the trigger of the new rule instance, which resolves its route again.
    [Fact]
    public async Task WhenASaveCarriesTheTriggersOverAndTheRulesAreDeactivatedThen_ThenOnlyTheLocationIsInTheUnion()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var room = new TestRoom { Sensor = new TestSensor { Gust = 0, Power = 0 }, Button = new TestButton() };
        host.Graph.Add("Room", room);
        var automation = await host.AddAutomationAsync("Carried", automation => automation.Rules = CreateCarriedRules());
        host.Weather.Gust = 9;
        await automation.FlushAsync();
        var unionBefore = Routes(automation).Union.ToHashSet();

        // Act
        await host.SaveAsync(CreateCarriedRules());
        var unionAfterTheSave = Routes(automation).Union.ToHashSet();
        automation.Enabled = false;
        await host.SaveAsync();

        // Assert
        Assert.Contains(new PropertyReference(room, nameof(TestRoom.Sensor)), unionBefore);
        Assert.Contains(new PropertyReference(room, nameof(TestRoom.Button)), unionBefore);
        Assert.Equal(unionBefore, unionAfterTheSave);
        Assert.Equal(Routes(automation).Location.Route.Properties.ToHashSet(), Routes(automation).Union.ToHashSet());
    }

    private static Dictionary<string, AutomationRule> CreateCarriedRules() => Keyed(
        ("gusts", On("Gusts", When("/Room/Sensor/Gust > 8"), Invoke("/Blinds/Close"))),
        ("press", Create("Press", AutomationRuleMode.On, [OnEvent("/Room/Button", "TestPressEvent")], [Invoke("/Blinds/Open")])),
        ("guarded", Create("Guarded", AutomationRuleMode.On, [When("/Weather/Gust > 8")], [Invoke("/Blinds/Close")], onlyIf: "/Room/Sensor/Power > 5")));

    [Fact]
    public async Task WhenAPathLeadsAboveTheAutomationAndItsParent_ThenEveryStructuralChangeReachesTheAutomation()
    {
        // Arrange
        await using var host = await AutomationTestHost.StartAsync(_ => { });
        var holder = new TestSensorHolder();
        var building = new TestFolder { Level = 9 };
        var floor = new TestFolder();
        host.Graph.Add("Holder", holder);
        host.Graph.Add("Building", building);
        building.Add("Floor", floor);
        var (automation, listener) = await AddCountedAsync(host, "Levels", Rule("../../../Level > 5"), automation => floor.Add("Levels", automation));
        listener.Reset();

        // Act
        holder.Sensor = new TestSensor();
        await automation.FlushAsync();

        // Assert
        Assert.True(Registration(automation).IsUnbounded);
        Assert.Equal(1, listener.GraphChanges);
        Assert.Equal(1, host.Blinds.CloseCount);
    }

    private static Action<Automation> Rule(string expression) => automation => automation.Rules = Keyed(
        ("rule", On("Rule", When(expression), Invoke("/Blinds/Close"))));

    private static GraphRoutes Routes(Automation automation) => automation.Runtime!.Routes;

    private static AutomationChangeDispatcher.Registration Registration(Automation automation) =>
        automation.Runtime!.Changes.Registration!;

    private static async Task<(Automation Automation, CountingListener Listener)> AddCountedAsync(
        AutomationTestHost host, string key, Action<Automation> configure, Action<Automation>? attach = null)
    {
        CountingListener? counting = null;
        host.ChangeDispatcher.ListenerDecorator = listener => counting = new CountingListener(listener);
        var automation = await host.AddAutomationAsync(key, configure, attach);
        host.ChangeDispatcher.ListenerDecorator = null;
        return (automation, counting!);
    }

    private sealed class CountingListener(IAutomationChangeListener inner) : IAutomationChangeListener
    {
        private readonly ConcurrentQueue<int> _graphChanges = new();
        private readonly ConcurrentQueue<string> _calls = new();

        public int GraphChanges => _graphChanges.Count;

        public int[] ChainDepths => [.. _graphChanges];

        public string[] Calls => [.. _calls];

        public void Reset()
        {
            _graphChanges.Clear();
            _calls.Clear();
        }

        public void OnWatchedPropertyChanged(PropertyChangedInput input)
        {
            _calls.Enqueue("watched");
            inner.OnWatchedPropertyChanged(input);
        }

        public void OnGraphChanged(int chainDepth)
        {
            _graphChanges.Enqueue(chainDepth);
            _calls.Enqueue("graph");
            inner.OnGraphChanged(chainDepth);
        }
    }
}
