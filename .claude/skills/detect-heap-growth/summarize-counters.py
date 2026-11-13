#!/usr/bin/env python3
"""Summarize a dotnet-counters CSV (System.Runtime) for the leak-detection precheck.

Reports managed heap trend (summed across generations), working-set delta,
and gen2 collection rate. Flags the "aggressive full-GC" regime where heap
values oscillate and simple first-vs-last deltas are unreliable.
"""
import csv
import re
import sys
from collections import defaultdict


def main():
    if len(sys.argv) != 2:
        print("Usage: summarize-counters.py <counters.csv>", file=sys.stderr)
        sys.exit(2)

    heap_by_ts = defaultdict(lambda: defaultdict(float))
    ws_by_ts = {}
    gen2_rate = []

    with open(sys.argv[1]) as f:
        r = csv.reader(f)
        next(r, None)
        for row in r:
            if len(row) < 5:
                continue
            ts, _, name, _, val = row
            try:
                v = float(val)
            except ValueError:
                continue
            m = re.search(r"dotnet\.gc\.last_collection\.heap\.size.*generation=(\w+)", name)
            if m:
                heap_by_ts[ts][m.group(1)] = v
                continue
            if "dotnet.process.memory.working_set" in name:
                ws_by_ts[ts] = v
                continue
            if "dotnet.gc.collections" in name and "gen2" in name:
                gen2_rate.append(v)

    timestamps = sorted(heap_by_ts)
    if not timestamps:
        print("No heap data in CSV - was the process running with diagnostics enabled?")
        sys.exit(1)

    totals = [sum(heap_by_ts[t].values()) for t in timestamps]
    first, last = totals[0], totals[-1]
    mn, mx = min(totals), max(totals)
    mean = sum(totals) / len(totals)

    print(f"Samples: {len(timestamps)} over {timestamps[0]} -> {timestamps[-1]}")
    print(
        f"Managed heap: first={first/1e6:.1f} MB, last={last/1e6:.1f} MB, "
        f"min={mn/1e6:.1f}, max={mx/1e6:.1f}, mean={mean/1e6:.1f}"
    )
    print(
        f"  Delta (last - first): {(last-first)/1e6:+.2f} MB "
        f"({((last-first)/first*100) if first else 0:+.1f}%)"
    )
    print(
        f"  Range (max - min):   {(mx-mn)/1e6:.2f} MB "
        f"({((mx-mn)/mean*100) if mean else 0:.1f}% of mean)"
    )

    if ws_by_ts:
        ws_ts = sorted(ws_by_ts)
        ws_first, ws_last = ws_by_ts[ws_ts[0]], ws_by_ts[ws_ts[-1]]
        print(
            f"Working set: first={ws_first/1e6:.1f} MB, last={ws_last/1e6:.1f} MB, "
            f"delta={(ws_last-ws_first)/1e6:+.2f} MB"
        )

    if gen2_rate:
        avg_per_2s = sum(gen2_rate) / len(gen2_rate)
        per_sec = avg_per_2s / 2
        print(f"Gen2 collections: avg {avg_per_2s:.1f}/2sec ({per_sec:.1f}/sec)")
        if per_sec > 1.0:
            print(
                "  WARNING: high gen2 rate - app may be calling GC.Collect() or "
                "under memory pressure. Heap values are noisy under this regime; "
                "do not trust a first-vs-last delta. Widen the window or use a "
                "longer log-based trend."
            )


if __name__ == "__main__":
    main()
