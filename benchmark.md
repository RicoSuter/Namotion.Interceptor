# OPC UA Library Upgrade Benchmark Results

Comparison of OPC UA Foundation library versions:
- **Master branch**: v1.5.376.244
- **PR #104 branch**: v1.5.378.10-preview

Benchmarks run on Ubuntu 24.04, .NET 9.0.308, after 5 minutes stabilization.

## Test Setup

Each setup was run for both branches:
1. **Bidirectional (Setup 1)**: 20k/s server→client + 20k/s client→server
2. **Server→Client only (Setup 2)**: 20k/s server→client, client worker disabled
3. **Client→Server only (Setup 3)**: 20k/s client→server, server worker disabled

---

## Setup 1: Bidirectional (20k/s each direction)

### Server Metrics (5th minute)

| Metric | Master | PR | Change |
|--------|--------|-----|--------|
| Received (avg changes/s) | 16,639 | 16,778 | +0.8% |
| End-to-end latency P50 (ms) | 296 | 340 | +15% |
| End-to-end latency P99 (ms) | 635 | 710 | +12% |
| Process memory (MB) | 731 | 764 | +4.5% |
| Allocations (MB/s) | 445 | 504 | **+13%** |

### Client Metrics (5th minute)

| Metric | Master | PR | Change |
|--------|--------|-----|--------|
| Received (avg changes/s) | 19,939 | 19,942 | ~same |
| End-to-end latency P50 (ms) | 68 | 75 | +10% |
| End-to-end latency P99 (ms) | 199 | 224 | +13% |
| Process memory (MB) | 520 | 591 | **+14%** |
| Allocations (MB/s) | 74 | 96 | **+30%** |

---

## Setup 2: Server→Client only (20k/s)

### Server Metrics (5th minute)

| Metric | Master | PR | Change |
|--------|--------|-----|--------|
| Published changes/s | ~19,800 | ~19,800 | ~same |
| Process memory (MB) | 546 | 786 | **+44%** |
| Allocations (MB/s) | 89 | 111 | **+25%** |

### Client Metrics (5th minute)

| Metric | Master | PR | Change |
|--------|--------|-----|--------|
| Received (avg changes/s) | 19,827 | 19,813 | ~same |
| End-to-end latency P50 (ms) | 39 | 41 | +5% |
| End-to-end latency P99 (ms) | 97 | 106 | +9% |
| Process memory (MB) | 469 | 500 | +6.6% |
| Allocations (MB/s) | 37 | 52 | **+41%** |

---

## Setup 3: Client→Server only (20k/s)

### Server Metrics (5th minute)

| Metric | Master | PR | Change |
|--------|--------|-----|--------|
| Received (avg changes/s) | 17,013 | 17,295 | +1.7% |
| End-to-end latency P50 (ms) | 233 | 265 | +14% |
| End-to-end latency P99 (ms) | 452 | 648 | **+43%** |
| Process memory (MB) | 860 | 798 | -7.2% |
| Allocations (MB/s) | 360 | 414 | **+15%** |

### Client Metrics (5th minute)

| Metric | Master | PR | Change |
|--------|--------|-----|--------|
| Published changes/s | ~17,000 | ~17,300 | +1.8% |
| Process memory (MB) | 490 | 486 | -1% |
| Allocations (MB/s) | 38 | 57 | **+50%** |

---

## Summary

### Memory Allocations

The upgraded OPC UA library shows **significantly higher memory allocations** across all setups:

| Setup | Component | Master (MB/s) | PR (MB/s) | Increase |
|-------|-----------|---------------|-----------|----------|
| Bidirectional | Server | 445 | 504 | +13% |
| Bidirectional | Client | 74 | 96 | +30% |
| Server→Client | Server | 89 | 111 | +25% |
| Server→Client | Client | 37 | 52 | +41% |
| Client→Server | Server | 360 | 414 | +15% |
| Client→Server | Client | 38 | 57 | +50% |

### Latency

End-to-end latencies increased moderately:
- **P50 latency**: +5% to +15% increase
- **P99 latency**: +9% to +43% increase (worst in client→server scenario)

### Throughput

Throughput remained essentially unchanged - both versions handle ~20k changes/second as expected.

### Conclusions

1. **Memory pressure increased significantly** - The new library version allocates 13-50% more memory depending on the scenario
2. **Latencies increased slightly** - P50 latencies up 5-15%, P99 (tail) latencies up 9-43%
3. **Throughput maintained** - The upgrade does not negatively impact throughput
4. **GC pressure concern** - The increased allocations could lead to more frequent GC pauses, explaining the higher tail latencies

### Recommendation

The PR author's observation is confirmed: while overall throughput is maintained, there is **significant increase in GC pressure** (+13% to +50% allocations) and **moderately higher latencies**. The increased P99 latencies (up to +43% in client→server scenario) suggest potential GC-related tail latency issues as noted in the PR description.
