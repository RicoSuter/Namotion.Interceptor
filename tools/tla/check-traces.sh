#!/usr/bin/env bash
# Validate every behavior in an ND-JSON trace file against a TLA+ model.
# Usage: check-traces.sh <traces.ndjson> <Model.tla>
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
traces="$1"; model="$2"
[ -f "$traces" ] || { echo "no trace file: $traces" >&2; exit 2; }
[ -f "$model" ]  || { echo "no model file: $model" >&2; exit 2; }
spec="$("$here/gen-trace-spec.sh" "$model")"
dir="$(cd "$(dirname "$spec")" && pwd)"; spec_name="$(basename "$spec" .tla)"
fail=0; n=0
while IFS= read -r line || [ -n "$line" ]; do
  [ -z "$line" ] && continue
  n=$((n + 1))
  one="$(mktemp --suffix=.ndjson)"; printf '%s\n' "$line" > "$one"
  if ! ( cd "$dir" && TRACE_PATH="$one" "$here/tlc" "$spec_name.tla" >/tmp/tlc.out 2>&1 ) || grep -qE "was violated|is violated" /tmp/tlc.out; then
    echo "NON-CONFORMING behavior #$n:"; tail -20 /tmp/tlc.out; fail=1
  fi
  rm -f "$one"; ( cd "$dir" && rm -rf states && rm -f "${spec_name}_TTrace_"*.tla "${spec_name}_TTrace_"*.bin )
done < "$traces"
( cd "$dir" && rm -f "$spec_name.tla" "$spec_name.cfg" )
echo "checked $n behavior(s)"; exit $fail
