# WebSocket TODO Fixes

Fixes for 4 TODOs in the WebSocket project.

## 1. Server Retry Loop

**File:** `WebSocketSubjectServer.cs:54`

**Problem:** Server crashes without retry if startup fails (port in use, permission denied).

**Solution:** Wrap server startup in retry loop with exponential backoff.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    var retryDelay = TimeSpan.FromSeconds(5);
    var maxRetryDelay = TimeSpan.FromSeconds(60);

    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            await RunServerAsync(stoppingToken);
            break;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket server failed. Retrying in {Delay}...", retryDelay);
            await Task.Delay(retryDelay, stoppingToken);

            var jitter = Random.Shared.NextDouble() * 0.1 + 0.95;
            retryDelay = TimeSpan.FromMilliseconds(
                Math.Min(retryDelay.TotalMilliseconds * 2 * jitter, maxRetryDelay.TotalMilliseconds));
        }
    }
}

private async Task RunServerAsync(CancellationToken stoppingToken)
{
    // Current ExecuteAsync body
}
```

## 2. Embed in Existing ASP.NET Server

**File:** `WebSocketSubjectServer.cs:65`

**Problem:** Always creates new Kestrel instance. Users with existing ASP.NET apps need two servers.

**Solution:** Add extension method to map WebSocket endpoint into existing app.

```csharp
public static IEndpointRouteBuilder MapWebSocketSubject<TSubject>(
    this IEndpointRouteBuilder endpoints,
    string path = "/ws",
    Action<WebSocketServerConfiguration>? configure = null)
    where TSubject : IInterceptorSubject
```

**Usage:**
```csharp
// Standalone (existing)
builder.Services.AddWebSocketSubjectServer<Device>(config => config.Port = 8080);

// Embedded (new)
app.UseWebSockets();
app.MapWebSocketSubject<Device>("/ws");
```

**Implementation:**
- Extract `WebSocketSubjectHandler` class for connection handling
- Both modes share the handler
- `ChangeQueueProcessor` managed via `IHostedService` when embedded

## 3. Remove Stale Client TODO

**File:** `WebSocketSubjectClientSource.cs:84`

**Problem:** TODO asks if retry is needed in `ConnectAsync`.

**Analysis:** Already handled correctly:
- Initial failures: `SubjectSourceBackgroundService` retries
- Post-startup disconnects: `ExecuteAsync` reconnects with exponential backoff

**Solution:** Remove the TODO.

## 4. Buffer Pooling

**File:** `WebSocketClientConnection.cs:103`

**Problem:** 64KB buffer allocated per receive. GC pressure at scale.

**Locations:**
- `WebSocketClientConnection.cs:103` - server receive
- `WebSocketSubjectClientSource.cs:110` - client handshake
- `WebSocketSubjectClientSource.cs:230` - client receive loop

**Solution:** Use `ArrayPool<byte>.Shared`:

```csharp
var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
try
{
    // receive logic
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```
