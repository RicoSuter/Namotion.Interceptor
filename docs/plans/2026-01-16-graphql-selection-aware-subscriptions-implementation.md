# GraphQL Selection-Aware Subscriptions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement selection-aware GraphQL subscriptions that only notify clients when properties within their selection set change.

**Architecture:** Add `GraphQLSubjectConfiguration` class following MQTT pattern. Rewrite `GraphQLSubscriptionSender` to use HotChocolate's `IAsyncEnumerable` pattern with per-client selection tracking. Use `CamelCasePathProvider` to match GraphQL field naming conventions.

**Tech Stack:** HotChocolate 15.x, System.Reactive, xUnit, Microsoft.AspNetCore.Mvc.Testing

---

## Task 1: Create GraphQL Test Project

**Files:**
- Create: `src/Namotion.Interceptor.GraphQL.Tests/Namotion.Interceptor.GraphQL.Tests.csproj`
- Modify: `src/Namotion.Interceptor.slnx` (add test project to /Tests/ folder)

**Step 1: Create test project file**

Create `src/Namotion.Interceptor.GraphQL.Tests/Namotion.Interceptor.GraphQL.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
        <IsTestProject>true</IsTestProject>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="9.0.1" />
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
        <PackageReference Include="xunit" Version="2.9.3" />
        <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
        <PackageReference Include="coverlet.collector" Version="6.0.4">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
        <ProjectReference Include="..\Namotion.Interceptor.GraphQL\Namotion.Interceptor.GraphQL.csproj"/>
        <ProjectReference Include="..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj"/>
    </ItemGroup>

</Project>
```

**Step 2: Add project to solution**

Add to `src/Namotion.Interceptor.slnx` inside the `/Tests/` folder section (around line 75):

```xml
    <Project Path="Namotion.Interceptor.GraphQL.Tests/Namotion.Interceptor.GraphQL.Tests.csproj" />
```

**Step 3: Verify build**

Run: `dotnet build src/Namotion.Interceptor.GraphQL.Tests/Namotion.Interceptor.GraphQL.Tests.csproj`
Expected: Build succeeded

**Step 4: Commit**

```
feat(graphql): add GraphQL test project scaffold
```

---

## Task 2: Create Test Model

**Files:**
- Create: `src/Namotion.Interceptor.GraphQL.Tests/Models/Sensor.cs`

**Step 1: Create test subject model**

Create `src/Namotion.Interceptor.GraphQL.Tests/Models/Sensor.cs`:

```csharp
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.GraphQL.Tests.Models;

[InterceptorSubject]
public partial class Sensor
{
    public partial decimal Temperature { get; set; }

    public partial decimal Humidity { get; set; }

    public partial Location? Location { get; set; }

    [Derived]
    public string Status => Temperature > 30 ? "Hot" : "Normal";
}

[InterceptorSubject]
public partial class Location
{
    public partial string? Building { get; set; }

    public partial string? Room { get; set; }
}
```

**Step 2: Verify build**

Run: `dotnet build src/Namotion.Interceptor.GraphQL.Tests/Namotion.Interceptor.GraphQL.Tests.csproj`
Expected: Build succeeded (source generator creates partial implementations)

**Step 3: Commit**

```
feat(graphql): add Sensor test model with nested Location
```

---

## Task 3: Create GraphQLSubjectConfiguration

**Files:**
- Create: `src/Namotion.Interceptor.GraphQL/GraphQLSubjectConfiguration.cs`
- Test: `src/Namotion.Interceptor.GraphQL.Tests/GraphQLSubjectConfigurationTests.cs`

**Step 1: Write failing test for configuration defaults**

Create `src/Namotion.Interceptor.GraphQL.Tests/GraphQLSubjectConfigurationTests.cs`:

```csharp
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.GraphQL.Tests;

public class GraphQLSubjectConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_HasExpectedDefaults()
    {
        // Act
        var config = new GraphQLSubjectConfiguration();

        // Assert
        Assert.Equal("root", config.RootName);
        Assert.Equal(TimeSpan.FromMilliseconds(50), config.BufferTime);
        Assert.IsType<CamelCasePathProvider>(config.PathProvider);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "GraphQLSubjectConfigurationTests"`
Expected: FAIL - `GraphQLSubjectConfiguration` type does not exist

**Step 3: Write minimal implementation**

Create `src/Namotion.Interceptor.GraphQL/GraphQLSubjectConfiguration.cs`:

```csharp
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.GraphQL;

/// <summary>
/// Configuration for GraphQL subject integration.
/// </summary>
public class GraphQLSubjectConfiguration
{
    /// <summary>
    /// Gets or sets the root field name for queries and subscriptions. Default is "root".
    /// </summary>
    public string RootName { get; init; } = "root";

    /// <summary>
    /// Gets or sets the path provider for property-to-GraphQL field mapping.
    /// Default uses camelCase to match GraphQL conventions.
    /// </summary>
    public IPathProvider PathProvider { get; init; } = CamelCasePathProvider.Instance;

    /// <summary>
    /// Gets or sets the time to buffer property changes before sending. Default is 50ms.
    /// Higher values batch more changes together, lower values reduce latency.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(50);
}
```

**Step 4: Add project reference to GraphQL project**

Modify `src/Namotion.Interceptor.GraphQL/Namotion.Interceptor.GraphQL.csproj`, add inside `<ItemGroup>` with ProjectReferences:

```xml
    <ProjectReference Include="..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
```

**Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "GraphQLSubjectConfigurationTests"`
Expected: PASS

**Step 6: Commit**

```
feat(graphql): add GraphQLSubjectConfiguration with defaults
```

---

## Task 4: Update Extension Methods with Configuration Overloads

**Files:**
- Modify: `src/Namotion.Interceptor.GraphQL/SubjectGraphQLExtensions.cs`
- Test: `src/Namotion.Interceptor.GraphQL.Tests/SubjectGraphQLExtensionsTests.cs`

**Step 1: Write failing test for new overloads**

Create `src/Namotion.Interceptor.GraphQL.Tests/SubjectGraphQLExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.GraphQL.Tests;

public class SubjectGraphQLExtensionsTests
{
    [Fact]
    public void AddSubjectGraphQL_WithRootName_ConfiguresCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(_ =>
        {
            var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
            return new Sensor(context);
        });

        // Act
        services.AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>("sensor");

        // Assert - just verify it builds without error
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public void AddSubjectGraphQL_WithFullConfiguration_ConfiguresCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddGraphQLServer()
            .AddSubjectGraphQL(
                sp =>
                {
                    var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
                    return new Sensor(context);
                },
                _ => new GraphQLSubjectConfiguration
                {
                    RootName = "mySensor",
                    BufferTime = TimeSpan.FromMilliseconds(100)
                });

        // Assert
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "SubjectGraphQLExtensionsTests"`
Expected: FAIL - overload with rootName parameter does not exist

**Step 3: Update SubjectGraphQLExtensions with new overloads**

Replace entire contents of `src/Namotion.Interceptor.GraphQL/SubjectGraphQLExtensions.cs`:

```csharp
using HotChocolate.Execution.Configuration;
using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.GraphQL;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectGraphQLExtensions
{
    /// <summary>
    /// Adds GraphQL support for the specified subject type using default configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(this IRequestExecutorBuilder builder)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration());
    }

    /// <summary>
    /// Adds GraphQL support for the specified subject type with a custom root name.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        string rootName)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration { RootName = rootName });
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector and configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        Func<IServiceProvider, GraphQLSubjectConfiguration> configurationProvider)
        where TSubject : IInterceptorSubject
    {
        var key = Guid.NewGuid().ToString();

        builder.Services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddSingleton<IHostedService>(sp =>
                new GraphQLSubscriptionSender<TSubject>(
                    sp.GetRequiredKeyedService<TSubject>(key),
                    sp.GetRequiredService<ITopicEventSender>(),
                    sp.GetRequiredKeyedService<GraphQLSubjectConfiguration>(key)));

        builder
            .AddQueryType<Query<TSubject>>()
            .AddSubscriptionType<Subscription<TSubject>>();

        return builder;
    }
}
```

**Step 4: Update GraphQLSubscriptionSender constructor**

Modify `src/Namotion.Interceptor.GraphQL/GraphQLSubscriptionSender.cs` to accept configuration:

```csharp
using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.GraphQL;

public class GraphQLSubscriptionSender<TSubject> : BackgroundService
    where TSubject : IInterceptorSubject
{
    private readonly TSubject _subject;
    private readonly ITopicEventSender _sender;
    private readonly GraphQLSubjectConfiguration _configuration;

    public GraphQLSubscriptionSender(
        TSubject subject,
        ITopicEventSender sender,
        GraphQLSubjectConfiguration configuration)
    {
        _subject = subject;
        _sender = sender;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var changes in _subject
            .Context
            .GetPropertyChangeObservable()
            .ToAsyncEnumerable()
            .WithCancellation(stoppingToken))
        {
            // TODO: Implement selection-aware filtering
            await _sender.SendAsync(
                _configuration.RootName,
                _subject,
                stoppingToken);
        }
    }
}
```

**Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "SubjectGraphQLExtensionsTests"`
Expected: PASS

**Step 6: Commit**

```
feat(graphql): add configuration overloads to extension methods
```

---

## Task 5: Update Query Class for Dynamic Root Name

**Files:**
- Modify: `src/Namotion.Interceptor.GraphQL/Query.cs`
- Test: `src/Namotion.Interceptor.GraphQL.Tests/QueryTests.cs`

**Step 1: Write failing test for dynamic root name**

Create `src/Namotion.Interceptor.GraphQL.Tests/QueryTests.cs`:

```csharp
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.GraphQL.Tests;

public class QueryTests
{
    [Fact]
    public async Task Query_WithCustomRootName_ReturnsSubject()
    {
        // Arrange
        var sensor = CreateSensor();
        sensor.Temperature = 25.5m;

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>("sensor")
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ sensor { temperature } }");

        // Assert
        Assert.Null(result.Errors);
        var data = result.ToJson();
        Assert.Contains("25.5", data);
    }

    [Fact]
    public async Task Query_WithDefaultRootName_ReturnsSubject()
    {
        // Arrange
        var sensor = CreateSensor();
        sensor.Temperature = 30.0m;

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>()
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act
        var result = await executor.ExecuteAsync("{ root { temperature } }");

        // Assert
        Assert.Null(result.Errors);
        var data = result.ToJson();
        Assert.Contains("30", data);
    }

    private static Sensor CreateSensor()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        return new Sensor(context);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "QueryTests"`
Expected: FAIL - Query uses hardcoded "root" name

**Step 3: Create dynamic query type with naming**

The Query class needs to be dynamically named. HotChocolate allows this via `[GraphQLName]` but we need runtime naming. We'll use HotChocolate's type extension pattern.

Replace `src/Namotion.Interceptor.GraphQL/Query.cs`:

```csharp
using HotChocolate;

namespace Namotion.Interceptor.GraphQL;

public class Query<TSubject>
{
    private readonly TSubject _subject;
    private readonly GraphQLSubjectConfiguration _configuration;

    public Query(TSubject subject, GraphQLSubjectConfiguration configuration)
    {
        _subject = subject;
        _configuration = configuration;
    }

    // Note: The field name is set dynamically via HotChocolate configuration
    [GraphQLName("root")]
    public TSubject GetRoot() => _subject;
}
```

Actually, HotChocolate doesn't support dynamic field naming at runtime easily. We need a different approach using `IObjectFieldDescriptor`. Let's use type interceptors.

Replace `src/Namotion.Interceptor.GraphQL/Query.cs`:

```csharp
namespace Namotion.Interceptor.GraphQL;

public class Query<TSubject>
{
    private readonly TSubject _subject;

    public Query(TSubject subject)
    {
        _subject = subject;
    }

    public TSubject GetRoot() => _subject;
}
```

Update `src/Namotion.Interceptor.GraphQL/SubjectGraphQLExtensions.cs` to configure field name:

```csharp
using HotChocolate.Execution.Configuration;
using HotChocolate.Subscriptions;
using HotChocolate.Types;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.GraphQL;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectGraphQLExtensions
{
    /// <summary>
    /// Adds GraphQL support for the specified subject type using default configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(this IRequestExecutorBuilder builder)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration());
    }

    /// <summary>
    /// Adds GraphQL support for the specified subject type with a custom root name.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        string rootName)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration { RootName = rootName });
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector and configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        Func<IServiceProvider, GraphQLSubjectConfiguration> configurationProvider)
        where TSubject : IInterceptorSubject
    {
        var key = Guid.NewGuid().ToString();

        builder.Services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddSingleton<IHostedService>(sp =>
                new GraphQLSubscriptionSender<TSubject>(
                    sp.GetRequiredKeyedService<TSubject>(key),
                    sp.GetRequiredService<ITopicEventSender>(),
                    sp.GetRequiredKeyedService<GraphQLSubjectConfiguration>(key)));

        // Get configuration for field naming
        GraphQLSubjectConfiguration? cachedConfig = null;
        GraphQLSubjectConfiguration GetConfig(IServiceProvider sp)
        {
            return cachedConfig ??= sp.GetRequiredKeyedService<GraphQLSubjectConfiguration>(key);
        }

        builder
            .AddQueryType(d => d
                .Name("Query")
                .Field("root")
                .Name(sp => GetConfig(sp).RootName)
                .Resolve(ctx =>
                {
                    var subject = ctx.Services.GetRequiredKeyedService<TSubject>(key);
                    return subject;
                }))
            .AddSubscriptionType(d => d
                .Name("Subscription")
                .Field("root")
                .Name(sp => GetConfig(sp).RootName)
                .Subscribe(ctx =>
                {
                    var config = ctx.Services.GetRequiredKeyedService<GraphQLSubjectConfiguration>(key);
                    return ctx.Services.GetRequiredService<ITopicEventReceiver>()
                        .SubscribeAsync<TSubject>(config.RootName, ctx.RequestAborted);
                })
                .Resolve(ctx => ctx.GetEventMessage<TSubject>()));

        return builder;
    }
}
```

Actually, HotChocolate's fluent API doesn't support `Name(Func<IServiceProvider, string>)`. Let me use a simpler approach - store the config synchronously.

**Step 3 (revised): Simplified approach with captured configuration**

Replace `src/Namotion.Interceptor.GraphQL/SubjectGraphQLExtensions.cs`:

```csharp
using HotChocolate.Execution.Configuration;
using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.GraphQL;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectGraphQLExtensions
{
    /// <summary>
    /// Adds GraphQL support for the specified subject type using default configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(this IRequestExecutorBuilder builder)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration());
    }

    /// <summary>
    /// Adds GraphQL support for the specified subject type with a custom root name.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        string rootName)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration { RootName = rootName });
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector and full configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        Func<IServiceProvider, GraphQLSubjectConfiguration> configurationProvider)
        where TSubject : IInterceptorSubject
    {
        // Create a deferred configuration that captures the root name at registration time
        // For now, we evaluate config with null service provider for the root name
        // This works because RootName is typically a static value
        var tempConfig = configurationProvider(null!);
        var rootName = tempConfig.RootName;

        var key = Guid.NewGuid().ToString();

        builder.Services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp))
            .AddSingleton<IHostedService>(sp =>
                new GraphQLSubscriptionSender<TSubject>(
                    sp.GetRequiredKeyedService<TSubject>(key),
                    sp.GetRequiredService<ITopicEventSender>(),
                    sp.GetRequiredKeyedService<GraphQLSubjectConfiguration>(key)));

        builder
            .AddQueryType(d => d
                .Name("Query")
                .Field(rootName)
                .Resolve(ctx => ctx.Services.GetRequiredKeyedService<TSubject>(key)))
            .AddSubscriptionType(d => d
                .Name("Subscription")
                .Field(rootName)
                .Subscribe(ctx => ctx.Services
                    .GetRequiredService<ITopicEventReceiver>()
                    .SubscribeAsync<TSubject>(rootName, ctx.RequestAborted))
                .Resolve(ctx => ctx.GetEventMessage<TSubject>()));

        return builder;
    }
}
```

**Step 4: Remove old Query and Subscription classes**

Delete `src/Namotion.Interceptor.GraphQL/Query.cs` and `src/Namotion.Interceptor.GraphQL/Subscription.cs` (they are now replaced by fluent configuration).

**Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "QueryTests"`
Expected: PASS

**Step 6: Commit**

```
feat(graphql): implement dynamic root name via fluent API
```

---

## Task 6: Implement Selection-Aware Subscription Filtering

**Files:**
- Modify: `src/Namotion.Interceptor.GraphQL/GraphQLSubscriptionSender.cs`
- Create: `src/Namotion.Interceptor.GraphQL/GraphQLSelectionMatcher.cs`
- Test: `src/Namotion.Interceptor.GraphQL.Tests/SubscriptionFilteringTests.cs`

**Step 1: Write failing test for selection filtering**

Create `src/Namotion.Interceptor.GraphQL.Tests/SubscriptionFilteringTests.cs`:

```csharp
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.GraphQL.Tests;

public class SubscriptionFilteringTests
{
    [Fact]
    public async Task Subscription_WhenSubscribedPropertyChanges_ReceivesUpdate()
    {
        // Arrange
        var sensor = CreateSensor();
        var executor = await CreateExecutorAsync(sensor);

        var subscriptionResult = await executor.ExecuteAsync(
            "subscription { root { temperature } }");

        var stream = subscriptionResult.ExpectResponseStream();
        var readTask = stream.ReadResultsAsync().GetAsyncEnumerator();

        // Act - change subscribed property
        sensor.Temperature = 42.0m;

        // Assert - should receive update
        var hasResult = await readTask.MoveNextAsync();
        Assert.True(hasResult);
        var result = readTask.Current;
        Assert.Null(result.Errors);
        Assert.Contains("42", result.ToJson());
    }

    [Fact]
    public async Task Subscription_WhenUnsubscribedPropertyChanges_DoesNotReceiveUpdate()
    {
        // Arrange
        var sensor = CreateSensor();
        var executor = await CreateExecutorAsync(sensor);

        var subscriptionResult = await executor.ExecuteAsync(
            "subscription { root { temperature } }");

        var stream = subscriptionResult.ExpectResponseStream();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act - change UNsubscribed property (humidity)
        sensor.Humidity = 80.0m;

        // Assert - should NOT receive update (timeout expected)
        var readTask = stream.ReadResultsAsync().GetAsyncEnumerator(cts.Token);
        var receivedUpdate = false;
        try
        {
            receivedUpdate = await readTask.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected - no update received
        }

        Assert.False(receivedUpdate, "Should not receive update for unsubscribed property");
    }

    private static Sensor CreateSensor()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        return new Sensor(context);
    }

    private static async Task<IRequestExecutor> CreateExecutorAsync(Sensor sensor)
    {
        return await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>()
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "SubscriptionFilteringTests"`
Expected: FAIL - unsubscribed property changes still trigger updates

**Step 3: Create selection matcher utility**

Create `src/Namotion.Interceptor.GraphQL/GraphQLSelectionMatcher.cs`:

```csharp
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.GraphQL;

/// <summary>
/// Matches property changes against GraphQL selection paths.
/// </summary>
public static class GraphQLSelectionMatcher
{
    /// <summary>
    /// Checks if a property change matches any of the selected paths.
    /// </summary>
    public static bool IsPropertyInSelection(
        SubjectPropertyChange change,
        IReadOnlySet<string> selectedPaths,
        IPathProvider pathProvider,
        IInterceptorSubject rootSubject)
    {
        var registeredProperty = change.Property.TryGetRegisteredProperty();
        if (registeredProperty is null)
        {
            return false;
        }

        // Build the path for this property change
        var pathParts = new List<string>();
        var current = registeredProperty;

        while (current is not null)
        {
            var segment = pathProvider.TryGetPropertySegment(current);
            if (segment is not null)
            {
                pathParts.Insert(0, segment);
            }

            if (ReferenceEquals(current.Parent.Subject, rootSubject))
            {
                break;
            }

            // Navigate to parent
            var parents = current.Parent.Parents;
            if (parents.Length > 0)
            {
                current = parents[0].Property;
            }
            else
            {
                break;
            }
        }

        if (pathParts.Count == 0)
        {
            return false;
        }

        var changePath = string.Join(".", pathParts);

        // Check for exact match or prefix match
        foreach (var selectedPath in selectedPaths)
        {
            // Exact match
            if (string.Equals(changePath, selectedPath, StringComparison.Ordinal))
            {
                return true;
            }

            // Changed property is parent of selected (e.g., "location" changed, "location.building" selected)
            if (selectedPath.StartsWith(changePath + ".", StringComparison.Ordinal))
            {
                return true;
            }

            // Changed property is child of selected (e.g., "location.building" changed, "location" selected)
            if (changePath.StartsWith(selectedPath + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts field paths from a GraphQL selection set.
    /// </summary>
    public static IReadOnlySet<string> ExtractSelectionPaths(
        IReadOnlyList<HotChocolate.Resolvers.ISelection> selections,
        string prefix = "")
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        ExtractPathsRecursive(selections, prefix, paths);
        return paths;
    }

    private static void ExtractPathsRecursive(
        IReadOnlyList<HotChocolate.Resolvers.ISelection> selections,
        string prefix,
        HashSet<string> paths)
    {
        foreach (var selection in selections)
        {
            var fieldName = selection.Field.Name;
            var path = string.IsNullOrEmpty(prefix) ? fieldName : $"{prefix}.{fieldName}";
            paths.Add(path);

            // Recurse into nested selections
            var childSelections = selection.SelectionSet?.Selections;
            if (childSelections?.Count > 0)
            {
                var typedSelections = childSelections
                    .OfType<HotChocolate.Resolvers.ISelection>()
                    .ToList();
                ExtractPathsRecursive(typedSelections, path, paths);
            }
        }
    }
}
```

**Step 4: Update SubjectGraphQLExtensions for selection-aware subscriptions**

This requires using `IAsyncEnumerable` pattern with per-client filtering. Update `src/Namotion.Interceptor.GraphQL/SubjectGraphQLExtensions.cs`:

```csharp
using System.Runtime.CompilerServices;
using HotChocolate.Execution.Configuration;
using HotChocolate.Subscriptions;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.GraphQL;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class SubjectGraphQLExtensions
{
    /// <summary>
    /// Adds GraphQL support for the specified subject type using default configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(this IRequestExecutorBuilder builder)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration());
    }

    /// <summary>
    /// Adds GraphQL support for the specified subject type with a custom root name.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        string rootName)
        where TSubject : IInterceptorSubject
    {
        return builder.AddSubjectGraphQL<TSubject>(
            sp => sp.GetRequiredService<TSubject>(),
            _ => new GraphQLSubjectConfiguration { RootName = rootName });
    }

    /// <summary>
    /// Adds GraphQL support with custom subject selector and full configuration.
    /// </summary>
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        Func<IServiceProvider, GraphQLSubjectConfiguration> configurationProvider)
        where TSubject : IInterceptorSubject
    {
        var tempConfig = configurationProvider(null!);
        var rootName = tempConfig.RootName;

        var key = Guid.NewGuid().ToString();

        builder.Services
            .AddKeyedSingleton(key, (sp, _) => configurationProvider(sp))
            .AddKeyedSingleton(key, (sp, _) => subjectSelector(sp));

        builder
            .AddQueryType(d => d
                .Name("Query")
                .Field(rootName)
                .Resolve(ctx => ctx.Services.GetRequiredKeyedService<TSubject>(key)))
            .AddSubscriptionType(d => d
                .Name("Subscription")
                .Field(rootName)
                .Resolve(ctx => ctx.GetEventMessage<TSubject>())
                .Subscribe(async ctx =>
                {
                    var subject = ctx.Services.GetRequiredKeyedService<TSubject>(key);
                    var config = ctx.Services.GetRequiredKeyedService<GraphQLSubjectConfiguration>(key);

                    // Extract selection paths from the subscription query
                    var selections = ctx.Selection.SelectionSet?.Selections
                        .OfType<HotChocolate.Resolvers.ISelection>()
                        .ToList() ?? [];
                    var selectedPaths = GraphQLSelectionMatcher.ExtractSelectionPaths(selections);

                    return CreateFilteredStream(subject, config, selectedPaths, ctx.RequestAborted);
                }));

        return builder;
    }

    private static async IAsyncEnumerable<TSubject> CreateFilteredStream<TSubject>(
        TSubject subject,
        GraphQLSubjectConfiguration config,
        IReadOnlySet<string> selectedPaths,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TSubject : IInterceptorSubject
    {
        var observable = subject.Context.GetPropertyChangeObservable();

        await foreach (var change in observable.ToAsyncEnumerable().WithCancellation(cancellationToken))
        {
            // Check if this change matches the client's selection
            if (GraphQLSelectionMatcher.IsPropertyInSelection(
                change, selectedPaths, config.PathProvider, subject))
            {
                // TODO: Apply BufferTime batching
                yield return subject;
            }
        }
    }
}
```

**Step 5: Add necessary using to GraphQL project**

Add `using Namotion.Interceptor.Registry;` import to any files that need it.

**Step 6: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "SubscriptionFilteringTests"`
Expected: PASS

**Step 7: Commit**

```
feat(graphql): implement selection-aware subscription filtering
```

---

## Task 7: Add Buffering Support

**Files:**
- Modify: `src/Namotion.Interceptor.GraphQL/SubjectGraphQLExtensions.cs`
- Test: `src/Namotion.Interceptor.GraphQL.Tests/BufferingTests.cs`

**Step 1: Write test for buffering behavior**

Create `src/Namotion.Interceptor.GraphQL.Tests/BufferingTests.cs`:

```csharp
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.GraphQL.Tests;

public class BufferingTests
{
    [Fact]
    public async Task Subscription_WithBuffering_BatchesRapidChanges()
    {
        // Arrange
        var sensor = CreateSensor();
        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL(
                sp => sp.GetRequiredService<Sensor>(),
                _ => new GraphQLSubjectConfiguration
                {
                    BufferTime = TimeSpan.FromMilliseconds(100)
                })
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        var subscriptionResult = await executor.ExecuteAsync(
            "subscription { root { temperature } }");

        var stream = subscriptionResult.ExpectResponseStream();
        var readTask = stream.ReadResultsAsync().GetAsyncEnumerator();

        // Act - rapid changes within buffer window
        sensor.Temperature = 10.0m;
        sensor.Temperature = 20.0m;
        sensor.Temperature = 30.0m;
        await Task.Delay(150); // Wait for buffer to flush

        // Assert - should receive at most 1-2 updates (batched), not 3
        var updateCount = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            while (await readTask.MoveNextAsync())
            {
                updateCount++;
                if (updateCount >= 3) break;
            }
        }
        catch (OperationCanceledException) { }

        Assert.True(updateCount < 3, $"Expected batched updates but got {updateCount}");
    }

    private static Sensor CreateSensor()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        return new Sensor(context);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "BufferingTests"`
Expected: FAIL - no buffering implemented

**Step 3: Add buffering to filtered stream**

Update the `CreateFilteredStream` method in `src/Namotion.Interceptor.GraphQL/SubjectGraphQLExtensions.cs`:

```csharp
private static async IAsyncEnumerable<TSubject> CreateFilteredStream<TSubject>(
    TSubject subject,
    GraphQLSubjectConfiguration config,
    IReadOnlySet<string> selectedPaths,
    [EnumeratorCancellation] CancellationToken cancellationToken)
    where TSubject : IInterceptorSubject
{
    var observable = subject.Context.GetPropertyChangeObservable();
    var hasRelevantChange = false;
    var lastYieldTime = DateTimeOffset.UtcNow;

    await foreach (var change in observable.ToAsyncEnumerable().WithCancellation(cancellationToken))
    {
        // Check if this change matches the client's selection
        if (GraphQLSelectionMatcher.IsPropertyInSelection(
            change, selectedPaths, config.PathProvider, subject))
        {
            hasRelevantChange = true;

            // Check if buffer time has elapsed
            var elapsed = DateTimeOffset.UtcNow - lastYieldTime;
            if (elapsed >= config.BufferTime)
            {
                hasRelevantChange = false;
                lastYieldTime = DateTimeOffset.UtcNow;
                yield return subject;
            }
        }
    }
}
```

Actually, this approach doesn't properly buffer. We need a more sophisticated approach using `System.Reactive.Linq.Buffer` or a timer-based approach. Let me revise:

```csharp
private static async IAsyncEnumerable<TSubject> CreateFilteredStream<TSubject>(
    TSubject subject,
    GraphQLSubjectConfiguration config,
    IReadOnlySet<string> selectedPaths,
    [EnumeratorCancellation] CancellationToken cancellationToken)
    where TSubject : IInterceptorSubject
{
    var observable = subject.Context.GetPropertyChangeObservable();

    // Use Rx Buffer operator for batching
    var buffered = observable
        .Where(change => GraphQLSelectionMatcher.IsPropertyInSelection(
            change, selectedPaths, config.PathProvider, subject))
        .Buffer(config.BufferTime)
        .Where(batch => batch.Count > 0);

    await foreach (var batch in buffered.ToAsyncEnumerable().WithCancellation(cancellationToken))
    {
        yield return subject;
    }
}
```

**Step 4: Add System.Reactive reference**

The GraphQL project already has `System.Linq.Async`. We need to ensure `System.Reactive` is available (it's a transitive dependency from Tracking).

**Step 5: Run test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests --filter "BufferingTests"`
Expected: PASS

**Step 6: Commit**

```
feat(graphql): add BufferTime support for batching rapid changes
```

---

## Task 8: Update Documentation

**Files:**
- Modify: `docs/graphql.md`

**Step 1: Update documentation with new features**

Append to `docs/graphql.md` before the Limitations section:

```markdown
## Configuration

### GraphQLSubjectConfiguration

```csharp
public class GraphQLSubjectConfiguration
{
    // Root field name for queries and subscriptions. Default: "root"
    public string RootName { get; init; } = "root";

    // Path provider for property-to-field mapping. Default: CamelCasePathProvider
    public IPathProvider PathProvider { get; init; } = CamelCasePathProvider.Instance;

    // Buffer time for batching rapid changes. Default: 50ms
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(50);
}
```

### Custom Root Name

```csharp
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Sensor>("sensor")  // Use "sensor" instead of "root"
    .AddInMemorySubscriptions();
```

Query:
```graphql
query { sensor { temperature } }
```

### Full Configuration

```csharp
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL(
        sp => sp.GetRequiredService<Sensor>(),
        sp => new GraphQLSubjectConfiguration
        {
            RootName = "mySensor",
            BufferTime = TimeSpan.FromMilliseconds(100),
            PathProvider = new AttributeBasedPathProvider("graphql", '.')
        })
    .AddInMemorySubscriptions();
```

## Selection-Aware Subscriptions

Subscriptions now only fire when properties within the client's selection set change.

```graphql
subscription {
  root {
    temperature  # Only notified when temperature changes
  }
}
```

If `humidity` changes but `temperature` doesn't, the subscription will **not** receive an update.

### Nested Selections

Nested property changes are correctly matched:

```graphql
subscription {
  root {
    location {
      building  # Notified when location.building changes
    }
  }
}
```

### Buffering

Rapid changes are batched according to `BufferTime`:

```csharp
new GraphQLSubjectConfiguration
{
    BufferTime = TimeSpan.FromMilliseconds(100)  // Batch changes within 100ms
}
```
```

**Step 2: Update Limitations section**

Replace the Limitations section:

```markdown
## Limitations

- Single subject per type (multiple subjects of same type not yet supported)
```

**Step 3: Commit**

```
docs(graphql): document selection-aware subscriptions and configuration
```

---

## Task 9: Clean Up Old Files

**Files:**
- Delete: `src/Namotion.Interceptor.GraphQL/Query.cs`
- Delete: `src/Namotion.Interceptor.GraphQL/Subscription.cs`
- Delete: `src/Namotion.Interceptor.GraphQL/GraphQLSubscriptionSender.cs`

**Step 1: Verify tests still pass after cleanup**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests`
Expected: All tests pass

**Step 2: Delete obsolete files**

Delete the following files (they've been replaced by fluent configuration):
- `src/Namotion.Interceptor.GraphQL/Query.cs`
- `src/Namotion.Interceptor.GraphQL/Subscription.cs`
- `src/Namotion.Interceptor.GraphQL/GraphQLSubscriptionSender.cs`

**Step 3: Verify build**

Run: `dotnet build src/Namotion.Interceptor.GraphQL`
Expected: Build succeeded

**Step 4: Commit**

```
refactor(graphql): remove obsolete Query/Subscription/Sender classes
```

---

## Task 10: Final Integration Test

**Files:**
- Test: `src/Namotion.Interceptor.GraphQL.Tests/IntegrationTests.cs`

**Step 1: Write comprehensive integration test**

Create `src/Namotion.Interceptor.GraphQL.Tests/IntegrationTests.cs`:

```csharp
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.GraphQL.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.GraphQL.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task FullScenario_QueryAndSubscription_WorksCorrectly()
    {
        // Arrange
        var sensor = CreateSensor();
        sensor.Temperature = 20.0m;
        sensor.Humidity = 50.0m;
        sensor.Location = new Location(sensor.Context) { Building = "A", Room = "101" };

        var executor = await new ServiceCollection()
            .AddSingleton(sensor)
            .AddGraphQLServer()
            .AddSubjectGraphQL<Sensor>("sensor")
            .AddInMemorySubscriptions()
            .BuildRequestExecutorAsync();

        // Act 1: Query
        var queryResult = await executor.ExecuteAsync(@"
            query {
                sensor {
                    temperature
                    humidity
                    location { building room }
                    status
                }
            }");

        // Assert 1: Query returns correct data
        Assert.Null(queryResult.Errors);
        var json = queryResult.ToJson();
        Assert.Contains("20", json);
        Assert.Contains("50", json);
        Assert.Contains("\"A\"", json);
        Assert.Contains("Normal", json);

        // Act 2: Subscribe to nested property
        var subscriptionResult = await executor.ExecuteAsync(@"
            subscription {
                sensor {
                    location { building }
                }
            }");

        var stream = subscriptionResult.ExpectResponseStream();
        var enumerator = stream.ReadResultsAsync().GetAsyncEnumerator();

        // Change nested property
        sensor.Location!.Building = "B";

        // Assert 2: Subscription receives update
        var hasResult = await enumerator.MoveNextAsync();
        Assert.True(hasResult);
        Assert.Contains("\"B\"", enumerator.Current.ToJson());
    }

    private static Sensor CreateSensor()
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        return new Sensor(context);
    }
}
```

**Step 2: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.GraphQL.Tests`
Expected: All tests pass

**Step 3: Run full solution tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests pass

**Step 4: Final commit**

```
test(graphql): add comprehensive integration tests
```

---

## Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Create test project | `.csproj`, `.slnx` |
| 2 | Create test model | `Models/Sensor.cs` |
| 3 | Create configuration class | `GraphQLSubjectConfiguration.cs` |
| 4 | Update extension methods | `SubjectGraphQLExtensions.cs` |
| 5 | Implement dynamic root name | `SubjectGraphQLExtensions.cs` |
| 6 | Implement selection filtering | `GraphQLSelectionMatcher.cs`, extensions |
| 7 | Add buffering support | Extensions update |
| 8 | Update documentation | `docs/graphql.md` |
| 9 | Clean up old files | Delete obsolete classes |
| 10 | Final integration test | `IntegrationTests.cs` |

**Estimated commits:** 10
