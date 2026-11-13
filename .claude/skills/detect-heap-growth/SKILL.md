---
name: detect-heap-growth
description: Use when a .NET process shows climbing RSS, OOM after extended runs, or a suspected memory leak — polls the managed heap across GC dumps, distinguishes real leaks from capacity stabilization, and escalates to fix-memory-leak when growth is linear.
---

# Detect Heap Growth

Polling check: is the managed heap growing, and if so, is the rate decelerating (capacity settling) or constant (real leak)?

## Prerequisites

- `dotnet-counters` (`dotnet tool install -g dotnet-counters`)
- `dotnet-gcdump` (`dotnet tool install -g dotnet-gcdump`)
- A running .NET process with diagnostics enabled

## Inputs

Ask the user for anything not already passed as args:

1. **Target** — PID or app name (e.g. `ConnectorTester`).
2. **Delay** — time between dumps. Pick so 5–20 units of work happen per window (30s for fast test harnesses, 5–10 min for production drift).
3. **Mode** — `single` (one dump pair → one verdict) or `continuous` (keep cycling until stop condition).
4. **Windows before escalation** (continuous only, default 3) — how many consecutive constant-rate windows before concluding "real leak."

## Step 1: Precheck with dotnet-counters

Before dumps (which briefly pause the process), collect a short counter trace to confirm the managed heap is actually growing. **Use `collect`, not `monitor`** — `monitor` is an interactive TUI that cannot be scripted, `collect` writes a CSV:

```bash
dotnet-counters collect -p $PID --counters System.Runtime --format csv \
  --refresh-interval 2 --duration 00:01:00 -o /tmp/counters.csv
```

Parse it:

```bash
python3 .claude/skills/detect-heap-growth/summarize-counters.py /tmp/counters.csv
```

The summarizer reports three signals that together decide whether to proceed:

- **Managed heap** — summed across gen0/gen1/gen2/loh/poh. In .NET 9+ the single pre-aggregated `gc-heap-size` counter is gone; heap size is reported per-generation via `dotnet.gc.last_collection.heap.size` and must be summed.
- **Working set** — `dotnet.process.memory.working_set`. Compare against the heap to tell managed from unmanaged growth.
- **Gen2 collection rate** — `dotnet.gc.collections` tagged `gen2`. Frequent full collections make heap values noisy and hide slow leaks.

Interpret:

- **Heap flat or oscillating around a stable mean, gen2 rate low** → no leak. Stop.
- **Working set grows, managed heap flat** → unmanaged allocation (P/Invoke, native buffers, interning). This skill does not apply — investigate native allocations instead.
- **Heap trending up over the window** → proceed to step 2.
- **Gen2 collections > ~1/sec** → the app is aggressively full-collecting (likely explicit `GC.Collect()` in code, or real memory pressure). The heap numbers in a 60-second window are unreliable here — a slow leak can easily read "flat" because every sample lands just after a full collection. Do not conclude "no leak" from this precheck alone. Either (a) find and remove the forced `GC.Collect()` and rerun, or (b) widen the window to minutes and fall through to the logs-based rolling-regression trend (bottom of this skill).

A 60-second window spots a fast leak. For slow leaks, rerun the precheck over several minutes, or skip to the gcdump comparison in steps 3–5 — which forces a full GC before each capture and so is not fooled by background collection rate.

## Step 2: Resolve PID

If a PID was given, verify its diagnostic socket exists:

```bash
ls /tmp/dotnet-diagnostic-$PID-* 2>/dev/null || echo "no diagnostic socket"
```

No socket → wrong PID (likely the bash/dotnet-run wrapper) or diagnostics disabled.

If an app name was given, filter to processes that both (a) have a diagnostic socket and (b) are the built assembly, not the launcher. When the app is started via `dotnet run`, both the `dotnet run` wrapper and the child app process have sockets — the wrapper's diagnostics report the launcher's runtime, not the app's heap, and will give meaningless numbers. Pick the process whose command line points at the built binary (`.../bin/.../AppName.dll` or the native binary), not the one with `dotnet run` or `--project` in its argv:

```bash
for pid in $(pgrep -f "$APPNAME"); do
    if ! ls /tmp/dotnet-diagnostic-$pid-* >/dev/null 2>&1; then continue; fi
    cmd=$(tr '\0' ' ' < /proc/$pid/cmdline 2>/dev/null)
    case "$cmd" in
        *"dotnet run"*|*"--project "*) continue ;;  # skip launcher
        */bin/*"$APPNAME"*|*"$APPNAME".dll*|*"$APPNAME") echo "$pid" ;;
    esac
done
```

Zero matches → app not running or diagnostics disabled.
Multiple matches → list them with their cmdlines and ask which.

In continuous mode, re-resolve the PID each cycle. The app may have been restarted.

## Step 3: Collect dumps

```bash
dotnet-gcdump collect -p $PID -o /tmp/heap-1.gcdump
sleep $DELAY
dotnet-gcdump collect -p $PID -o /tmp/heap-2.gcdump
```

`dotnet-gcdump` forces a full GC (including finalizers) before capture — counts reflect live objects. Do not add `GC.Collect()` calls to app code.

In continuous mode use **rolling windows** — always compare the latest dump to the previous one. Absolute growth from t=0 stays positive even after the heap stabilizes; only window-over-window rate answers "is it still growing?"

## Step 4: Compare

```bash
python3 .claude/skills/detect-heap-growth/diff-gcdumps.py /tmp/heap-1.gcdump /tmp/heap-2.gcdump
```

Output lists the top growing types by byte delta and count delta.

## Step 5: Interpret

| Signal | Meaning |
|--------|---------|
| Count stable, size grew | Dictionary/HashSet capacity resize — usually stabilizes |
| Count grows proportionally to time/cycles | Objects accumulating — likely leak |
| Strings growing | Interning, serialization caches, IDs not released |
| `Entry<K,V>[]` growing | Dictionary backing arrays — check `.Clear()` vs. recreation |
| `VolatileNode<K,V>` growing | ConcurrentDictionary nodes — check if `Remove` is called |

## Step 6: Stop condition

- **Decelerating rate across windows** → capacity settling. Exit: "not a leak."
- **Constant or growing rate across N consecutive windows** (default N=3) → real leak. Exit: invoke `fix-memory-leak`, passing the top growing types from step 5.
- **Single mode inconclusive** (one window, ambiguous) → report and suggest running in continuous mode.

## Running continuously

Harness-agnostic. Any driver works: `/loop`, cron, systemd timer, plain bash `while`.

- **Intra-invocation delay**: use `sleep` for waits under ~4 minutes. Keeps the prompt cache warm and Claude interactive.
- **Longer waits**: exit the skill and let an external driver re-invoke it. Do not hold a 30-minute sleep inside one Bash call.

## Growth rate from logs (optional)

If the process already logs heap size per unit of work (e.g. a `HeapMB` per cycle counter), a linear regression over a rolling window answers "accelerating, stable, or decelerating?" more cheaply than repeated gcdumps. Same verdict rules in step 6 apply.

```python
python3 << 'PYEOF'
import re
with open('logs/memory.log') as f:
    lines = f.readlines()

data = []
for line in lines:
    m = re.search(r'Cycle (\d+).*HeapMB: ([\d.]+)', line)
    if m:
        data.append((int(m.group(1)), float(m.group(2))))

print(f"{'Window':>12} {'Rate KB/cyc':>12} {'Avg HeapMB':>12}")
for start in range(0, len(data) - 100, 200):
    chunk = data[start:start + 100]
    x = [d[0] for d in chunk]
    y = [d[1] for d in chunk]
    n = len(chunk)
    x_mean = sum(x) / n
    y_mean = sum(y) / n
    num = sum((xi - x_mean) * (yi - y_mean) for xi, yi in zip(x, y))
    den = sum((xi - x_mean) ** 2 for xi in x)
    slope = num / den if den > 0 else 0
    print(f"{chunk[0][0]}-{chunk[-1][0]:>5} {slope * 1000:>12.1f} {y_mean:>12.1f}")
PYEOF
```
