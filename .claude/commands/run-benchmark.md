# Run Protocol Benchmark

Run a performance benchmark for OPC UA or MQTT protocol. The argument specifies which protocol to test.

## Usage
`/run-benchmark <protocol>`

Where `<protocol>` is one of:
- `opc-ua` - Run OPC UA server/client benchmark
- `mqtt` - Run MQTT server/client benchmark

## Instructions

When the user invokes this command, follow these steps:

### 1. Build the Solution
```bash
cd "C:\Users\rsute\GitHub\Namotion.Interceptor\src" && dotnet build Namotion.Interceptor.sln -c Release
```

### 2. Start the Appropriate Server (in background)

**For OPC UA:**
```bash
cd "C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa.SampleServer" && dotnet run -c Release
```

**For MQTT:**
```bash
cd "C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.Mqtt.SampleServer" && dotnet run -c Release
```

### 3. Wait for Server Initialization
Wait approximately 5 seconds for the server to start and be ready to accept connections.

### 4. Start the Client (in background)

**For OPC UA:**
```bash
cd "C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa.SampleClient" && dotnet run -c Release
```

**For MQTT:**
```bash
cd "C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.Mqtt.SampleClient" && dotnet run -c Release
```

### 5. Wait for Results
**IMPORTANT:** Always wait at least 2-3 minutes (until you see the 3rd "1 minute" result) before reporting results. The first 1-2 minutes include warmup and initialization overhead.

Check both server and client outputs periodically (every 30-60 seconds) using BashOutput.

### 6. Report Key Metrics
After collecting stable results (3rd minute or later), report these key metrics in a summary table:

| Metric | Server | Client |
|--------|--------|--------|
| Received (changes/s) | avg, P50 | avg, P50 |
| Processing latency (ms) | avg, P50 | avg, P50 |
| End-to-end latency (ms) | avg, P50 | avg, P50 |
| Memory | heap MB | heap MB |
| Allocations | MB/s | MB/s |

### 7. Cleanup
After collecting results, kill both the server and client background processes using KillShell.

## Key Metrics to Watch

- **End-to-end latency (ms):** Total time from property change to receiving the update. Lower is better.
- **Processing latency (ms):** Time spent in library code processing the change. Should be negligible (<1ms).
- **Received (changes/s):** Throughput of received changes being processed. Should be 20000.
- **Memory/Allocations:** Monitor for memory leaks or excessive allocations.

## Notes

- Server receives 20000/s updates from client (client -> server)
- Client receives 20000/s updates from server (server -> client)
- OPC UA benchmark uses ~40,000 monitored items
- MQTT benchmark uses similar scale
