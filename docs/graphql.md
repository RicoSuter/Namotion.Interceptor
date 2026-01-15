# Namotion.Interceptor.GraphQL

Real-time GraphQL integration using [HotChocolate](https://chillicream.com/docs/hotchocolate). Exposes interceptor subjects as GraphQL queries with automatic subscription support for property changes.

## Getting Started

### Installation

Add the `Namotion.Interceptor.GraphQL` package to your ASP.NET Core project:

```xml
<PackageReference Include="Namotion.Interceptor.GraphQL" Version="0.1.0" />
```

### Basic Usage

Configure GraphQL with your subject:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register your subject with change tracking
builder.Services.AddSingleton(sp =>
{
    var context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking();
    return new Sensor(context);
});

// Add GraphQL with subject support
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Sensor>()
    .AddInMemorySubscriptions();

var app = builder.Build();

app.MapGraphQL();

app.Run();
```

This creates:
- A `root` query that returns the subject
- A `root` subscription that streams updates on property changes

## Features

### Query

Fetch the current subject state:

```graphql
query {
  root {
    temperature
    humidity
    location {
      building
      room
    }
  }
}
```

Response:
```json
{
  "data": {
    "root": {
      "temperature": 25.5,
      "humidity": 60,
      "location": {
        "building": "A",
        "room": "101"
      }
    }
  }
}
```

### Subscription

Subscribe to real-time updates. The subscription fires whenever any property in the subject graph changes:

```graphql
subscription {
  root {
    temperature
    humidity
  }
}
```

Each time a property changes, the subscription receives the updated subject:

```json
{
  "data": {
    "root": {
      "temperature": 26.0,
      "humidity": 58
    }
  }
}
```

## API Reference

### AddSubjectGraphQL

```csharp
// Use registered service
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<TSubject>();

// Use custom selector
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<TSubject>(
        Func<IServiceProvider, TSubject> subjectSelector);
```

### Generated Types

The extension registers:

| Type | Description |
|------|-------------|
| `Query<TSubject>` | Query type with `GetRoot()` resolver |
| `Subscription<TSubject>` | Subscription type with `Root` topic |
| `GraphQLSubscriptionSender<TSubject>` | Background service that publishes changes |

## Requirements

### Change Tracking

The subject context must have property change tracking enabled:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking();  // Required for subscriptions
```

### In-Memory Subscriptions

For subscriptions to work, add HotChocolate's subscription provider:

```csharp
builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Sensor>()
    .AddInMemorySubscriptions();  // Required
```

For production with multiple server instances, use Redis or another distributed subscription provider.

## How It Works

1. `GraphQLSubscriptionSender<TSubject>` runs as a background service
2. It subscribes to `GetPropertyChangeObservable()` on the subject's context
3. When any property changes, it publishes the entire subject to the `Root` topic
4. HotChocolate delivers the update to all active subscription clients

## Example: Real-Time Dashboard

```csharp
[InterceptorSubject]
public partial class Dashboard
{
    public partial decimal CpuUsage { get; set; }
    public partial decimal MemoryUsage { get; set; }
    public partial int ActiveConnections { get; set; }

    [Derived]
    public string Status => CpuUsage > 90 ? "Critical" : "Normal";
}
```

```csharp
// Program.cs
builder.Services.AddSingleton(sp =>
{
    var context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking();
    return new Dashboard(context);
});

builder.Services
    .AddGraphQLServer()
    .AddSubjectGraphQL<Dashboard>()
    .AddInMemorySubscriptions();
```

Frontend subscription:
```graphql
subscription {
  root {
    cpuUsage
    memoryUsage
    activeConnections
    status
  }
}
```

Changes to any property (including derived properties like `Status`) automatically push updates to connected clients.

## Limitations

- Currently sends the entire subject on each change (delta updates planned)
- Single subject per type (multiple subjects of same type not yet supported)
