#!/usr/bin/env python3
"""Diff two dotnet-gcdump files and print the top growing types by byte delta."""
import subprocess
import re
import sys


def parse_dump(path):
    result = subprocess.run(
        ["dotnet-gcdump", "report", path], capture_output=True, text=True
    )
    types = {}
    for line in result.stdout.split("\n"):
        m = re.match(r"\s+([\d,]+)\s+(\d+)\s+(.+)", line)
        if m:
            bytes_val = int(m.group(1).replace(",", ""))
            count = int(m.group(2))
            type_name = m.group(3).strip()
            clean = re.sub(r"\s*\(Bytes > \w+\)\s*", " ", type_name).strip()
            clean = re.sub(r"\s*\[.*?\]\s*$", "", clean).strip()
            if clean in types:
                types[clean] = (types[clean][0] + bytes_val, types[clean][1] + count)
            else:
                types[clean] = (bytes_val, count)
    return types


def main():
    if len(sys.argv) != 3:
        print(
            "Usage: diff-gcdumps.py <before.gcdump> <after.gcdump>", file=sys.stderr
        )
        sys.exit(2)

    d1 = parse_dump(sys.argv[1])
    d2 = parse_dump(sys.argv[2])

    diffs = []
    for t in set(d1.keys()) | set(d2.keys()):
        b1, c1 = d1.get(t, (0, 0))
        b2, c2 = d2.get(t, (0, 0))
        if b2 - b1 > 1000:
            diffs.append((b2 - b1, c2 - c1, b2, c2, t))

    diffs.sort(reverse=True)
    print(f"{'Growth':>10} {'Count+':>8} {'Total':>10} {'Count':>8}  Type")
    for db, dc, b2, c2, t in diffs[:20]:
        print(f"{db:>10} {dc:>+8} {b2:>10} {c2:>8}  {t}")

    total_growth = sum(db for db, _, _, _, _ in diffs)
    print(f"\nTotal growth: {total_growth:,} bytes across {len(diffs)} types")


if __name__ == "__main__":
    main()
