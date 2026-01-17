# GraphQL Selection-Aware Subscriptions Design

## Overview

Enhance the GraphQL integration to support selection-aware subscription filtering. Instead of sending the entire subject on every property change, only notify clients when properties within their subscription selection change.

## Problem Statement

Current limitations (from `docs/graphql.md`):
1. **Sends entire subject on each change** — Every property change triggers a subscription update, even if the client didn't subscribe to that property
2. **Hardcoded root name** — The `root` field name cannot be customized

## Goals

1. Only send subscription updates when a property within the client's selection set changes
2. Make the root field name configurable
3. Follow existing patterns (MQTT configuration, path providers)
4. Add integration tests

## Design

### Configuration Class

```csharp
namespace Namotion.Interceptor.GraphQL;

public class GraphQLSubjectConfiguration
{
    /// <summary>
    /// Gets or sets the root field name for queries and subscriptions. Default is "root".
    /// </summary>
    public string RootName { get; init; } = "root";

    /// <summary>
    /// Gets or sets the path provider for property-to-GraphQL field mapping.
    /// Default uses camelCase to match GraphQL conventions.
    /// Use IsPropertyIncluded to filter out properties (e.g., derived properties).
    /// </summary>
    public PathProviderBase PathProvider { get; init; } = CamelCasePathProvider.Instance;

    /// <summary>
    /// Gets or sets the time to buffer property changes before sending. Default is 50ms.
    /// Higher values batch more changes together, lower values reduce latency.
    /// </summary>
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromMilliseconds(50);
}
```

### Extension Methods API

Following the MQTT pattern with simple and advanced overloads:

```csharp
public static class SubjectGraphQLExtensions
{
    // Simple: use registered service, default config
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder)
        where TSubject : IInterceptorSubject;

    // Simple with root name override
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        string rootName)
        where TSubject : IInterceptorSubject;

    // Advanced: custom subject selector and full configuration
    public static IRequestExecutorBuilder AddSubjectGraphQL<TSubject>(
        this IRequestExecutorBuilder builder,
        Func<IServiceProvider, TSubject> subjectSelector,
        Func<IServiceProvider, GraphQLSubjectConfiguration> configurationProvider)
        where TSubject : IInterceptorSubject;
}
```

### Usage Examples

```csharp
// Simple usage with defaults
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Sensor>();

// Custom root name
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Sensor>(rootName: "sensor");

// Full configuration
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL(
        sp => sp.GetRequiredService<Sensor>(),
        sp => new GraphQLSubjectConfiguration
        {
            RootName = "sensor",
            BufferTime = TimeSpan.FromMilliseconds(100),
            PathProvider = new AttributeBasedPathProvider("graphql", '.')
        });
```

### Selection-Aware Filtering Algorithm

1. Client subscribes with GraphQL selection (e.g., `{ temperature, location { building } }`)
2. Extract paths from selection set: `["temperature", "location.building"]`
3. When a property changes, convert to path using `PathProvider`
4. Check if the changed path matches any subscribed path:
   - Exact match: `"temperature"` matches `"temperature"`
   - Prefix match: `"location"` changes should notify `"location.building"` subscribers
5. Only yield to clients whose selection includes the changed property

### Edge Cases

- **Parent object changes**: When `location` is reassigned, notify all subscribers to `location.*` paths
- **Collection changes**: Match paths with indices (e.g., `items[0].name`)
- **Derived properties**: Included by default; filter via `PathProvider.IsPropertyIncluded()`

## Implementation Components

| Component | Description |
|-----------|-------------|
| `GraphQLSubjectConfiguration` | New configuration class |
| `SubjectGraphQLExtensions` | Updated extension methods with overloads |
| `GraphQLSubscriptionSender<T>` | Rewritten with selection-aware filtering and buffering |
| `Subscription<T>` | Updated with dynamic root name and `IAsyncEnumerable` pattern |
| `Query<T>` | Updated with dynamic root name |
| `Namotion.Interceptor.GraphQL.Tests` | New test project |

### GraphQLSubscriptionSender Changes

The sender will:
1. Use `IAsyncEnumerable` with `[Subscribe(With = ...)]` pattern for per-client filtering
2. Capture each client's selection set when they subscribe
3. Convert property changes to paths using the configured `PathProvider`
4. Only yield updates when the changed path intersects with the client's selection
5. Buffer changes using `BufferTime` before sending

### Test Project Structure

```
Namotion.Interceptor.GraphQL.Tests/
├── Models/
│   └── Sensor.cs                    # Test subject
├── GraphQLQueryTests.cs             # Query functionality
├── GraphQLSubscriptionTests.cs      # Subscription filtering
└── GraphQLConfigurationTests.cs     # Configuration validation
```

Key test scenarios:
- Query returns subject with correct root name
- Subscription fires when subscribed property changes
- Subscription does NOT fire when unsubscribed property changes
- Nested property changes correctly matched
- BufferTime batches rapid changes
- Custom PathProvider filtering works

## Documentation Updates

Update `docs/graphql.md` to include:
- New configuration options
- Selection-aware filtering behavior
- Migration notes (breaking change: subscription now filters)
- Examples with custom configuration

## Dependencies

No new dependencies required. Uses existing:
- `Namotion.Interceptor.Registry.Paths` for path providers
- HotChocolate's `IAsyncEnumerable` subscription pattern

## Open Questions

None — design validated through brainstorming session.

## References

- MQTT configuration pattern: `src/Namotion.Interceptor.Mqtt/Client/MqttClientConfiguration.cs`
- Path providers: `src/Namotion.Interceptor.Registry/Paths/`
- HotChocolate subscriptions: https://chillicream.com/docs/hotchocolate/v14/defining-a-schema/subscriptions/
