# OPC UA Client Trace Validation, Phase 2a: The Checking Pipeline

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify the trace-checking pipeline that validates a JSON execution trace against `OpcUaClient.tla` with TLC, proven end to end against hand-written sample traces.

**Architecture:** The model is the single source of truth. A generator reads the model's `VARIABLES` declaration and emits a trace-check module that pins each variable to the recorded values and requires each recorded step to satisfy the model's `Next`. Because we derive the item set per trace and emit full snapshots, every variable pins uniformly (`v = Trace[i].v`), so the generator needs no per-variable special casing and there is nothing to hand-keep in sync. A driver script runs it per behavior. No C# and no production code in this phase.

**Tech Stack:** TLA+ / TLC 2.19 (rootless under `tools/tla/`), TLA+ Community Modules (`CommunityModules-deps.jar`), bash.

**Scope:** Phase 2a. Phase 2b (the `[Conditional("MODELTRACE")]` C# helper, the sink, and instrumenting the client) is a separate plan. See `docs/superpowers/specs/2026-07-08-opcua-client-trace-validation-design.md`.

**Prerequisite:** `tools/tla/bootstrap.sh` and `tools/tla/tlc` exist and pass `tools/tla/tlc selftest/Smoke.tla`. `docs/formal/opcua-client/OpcUaClient.tla` exists (Phase 1).

**Note on TLA+ authoring:** the generated trace-check module and the sample traces are converged against TLC. Each task gives concrete code, the exact command, and the expected result; the troubleshooting note names the usual cause of a failure. Iterating against TLC is expected, not a plan failure.

---

## File Structure

- Modify: `tools/tla/bootstrap.sh` — also fetch pinned `CommunityModules-deps.jar`.
- Modify: `tools/tla/tlc` — add the community jar to the classpath.
- Create: `tools/tla/gen-trace-spec.sh` — generates a trace-check module from a model's `VARIABLES` (the model stays the single source of truth; nothing is hand-synced).
- Create: `tools/tla/check-traces.sh` — the driver: generate, then run TLC per behavior.
- Create: `docs/formal/opcua-client/samples/*.ndjson` — hand-written conforming and non-conforming traces.
- Modify: `docs/formal/README.md` — add the trace-validation section.
- Gitignore: `docs/formal/**/*Trace.tla` and `*Trace.cfg` (generated artifacts).

---

### Task 1: Add the Community Modules to the toolchain

**Files:**
- Modify: `tools/tla/bootstrap.sh`
- Modify: `tools/tla/tlc`

- [ ] **Step 1: Find and pin the Community Modules jar**

Run:

```bash
url="$(curl -s https://api.github.com/repos/tlaplus/CommunityModules/releases/latest | grep -o 'https://[^"]*CommunityModules-deps\.jar')"
echo "$url"; curl -sSL -o /tmp/cm.jar "$url"; sha256sum /tmp/cm.jar
```

Expected: a URL and a sha256. Note both.

- [ ] **Step 2: Add the download to `bootstrap.sh`**

In `tools/tla/bootstrap.sh`, after the `tla2tools.jar` block, insert (fill `<URL>`/`<SHA>` from Step 1):

```bash
CM_URL="<URL>"
CM_SHA256="<SHA>"
if [ -f "$cache/CommunityModules-deps.jar" ] && verify "$cache/CommunityModules-deps.jar" "$CM_SHA256"; then
  echo "CommunityModules: cached"
else
  echo "CommunityModules: downloading"
  curl -sSL --max-time 180 -o "$cache/CommunityModules-deps.jar" "$CM_URL"
  verify "$cache/CommunityModules-deps.jar" "$CM_SHA256" || { echo "checksum mismatch: CommunityModules" >&2; exit 1; }
fi
```

- [ ] **Step 3: Add the jar to the `tlc` classpath**

In `tools/tla/tlc`, change the final `exec` to include the community jar:

```bash
cm="$cache/CommunityModules-deps.jar"
exec "$java_bin" -XX:+UseParallelGC -cp "$jar:$cm" tlc2.TLC "$@"
```

- [ ] **Step 4: Bootstrap and confirm the `Json` module loads**

Run:

```bash
tools/tla/bootstrap.sh
printf '%s\n' '---- MODULE JsonSmoke ----' 'EXTENDS Json' 'X == ToJson([a |-> 1])' '====' > tools/tla/selftest/JsonSmoke.tla
( cd tools/tla/selftest && ../../tla/tlc -eval 'X' JsonSmoke.tla 2>&1 | tail -4 )
```

Expected: a JSON string like `{"a":1}`, no "Unknown module Json". If it errors, the jar is not on the classpath (recheck Step 3).

- [ ] **Step 5: Commit**

```bash
git add tools/tla/bootstrap.sh tools/tla/tlc tools/tla/selftest/JsonSmoke.tla
git commit -m "Add TLA+ Community Modules to the toolchain for trace validation"
```

---

### Task 2: Hand-written sample traces

**Files:**
- Create: `docs/formal/opcua-client/samples/ok.ndjson`
- Create: `docs/formal/opcua-client/samples/bad.ndjson`

Each line is one behavior: a JSON array of full-state snapshots. Every snapshot carries all model variables under matching field names. `cover` keys are realistic node keys.

- [ ] **Step 1: Write a conforming trace (Connect then Activate)**

Create `docs/formal/opcua-client/samples/ok.ndjson` (one line):

```
[{"seq":0,"state":"Disconnected","linkUp":true,"cover":{"n1":"Retrying","n2":"Retrying"},"buffering":false,"stalled":false},{"seq":1,"state":"Connecting","linkUp":true,"cover":{"n1":"Retrying","n2":"Retrying"},"buffering":false,"stalled":false},{"seq":2,"state":"SessionActive","linkUp":true,"cover":{"n1":"Subscribed","n2":"Subscribed"},"buffering":false,"stalled":false}]
```

- [ ] **Step 2: Write a non-conforming trace (illegal jump)**

Create `docs/formal/opcua-client/samples/bad.ndjson` (one line): a direct `Disconnected -> SessionActive`, which no action permits.

```
[{"seq":0,"state":"Disconnected","linkUp":true,"cover":{"n1":"Retrying","n2":"Retrying"},"buffering":false,"stalled":false},{"seq":1,"state":"SessionActive","linkUp":true,"cover":{"n1":"Subscribed","n2":"Subscribed"},"buffering":false,"stalled":false}]
```

- [ ] **Step 3: Commit**

```bash
git add docs/formal/opcua-client/samples/ok.ndjson docs/formal/opcua-client/samples/bad.ndjson
git commit -m "Add hand-written conforming and non-conforming sample traces"
```

---

### Task 3: The trace-spec generator

**Files:**
- Create: `tools/tla/gen-trace-spec.sh`
- Modify: `.gitignore`

The generator reads the model's `VARIABLES` and emits `<ModelDir>/<Model>Trace.tla` and `.cfg`. Uniform pinning means no per-variable special casing; the one model-specific line is deriving `Items` from the trace's `cover` domain.

- [ ] **Step 1: Write the generator**

Create `tools/tla/gen-trace-spec.sh`:

```bash
#!/usr/bin/env bash
# Generate a trace-check module from a model's VARIABLES. The model is the single
# source of truth; the trace module is derived (nothing hand-synced).
# Usage: gen-trace-spec.sh <Model.tla>  ->  writes <ModelDir>/<Model>Trace.tla(+.cfg)
set -euo pipefail
model="$1"
dir="$(cd "$(dirname "$model")" && pwd)"
name="$(basename "$model" .tla)"

# Variable names from the VARIABLES block (one per line, strip \* comments and commas).
vars=$(awk '
  f && /^[A-Za-z=]/ {f=0}
  f { s=$0; sub(/\\\*.*/,"",s); gsub(/[ \t,]/,"",s); if (s!="") print s }
  /^VARIABLES?[ \t]*$/ {f=1}
' "$model")

{
  echo "---- MODULE ${name}Trace ----"
  echo "EXTENDS ${name}, Sequences, Naturals, Json, IOUtils, TLC"
  echo "Trace == ndJsonDeserialize(IOEnv.TRACE_PATH)[1]"
  echo "TraceItems == DOMAIN Trace[1].cover"
  echo "VARIABLE l"
  printf "TraceInit == l = 1"
  for v in $vars; do printf ' /\\ %s = Trace[1].%s' "$v" "$v"; done; echo
  printf "TraceNext == l < Len(Trace) /\\ l' = l + 1"
  for v in $vars; do printf " /\\ %s' = Trace[l+1].%s" "$v" "$v"; done
  printf ' /\\ Next\n'
  echo "TraceSpec == TraceInit /\\ [][TraceNext]_<<vars, l>>"
  echo "Conforms == l = Len(Trace)"
  echo "===="
} > "$dir/${name}Trace.tla"

cat > "$dir/${name}Trace.cfg" <<EOF
CONSTANTS Items <- TraceItems
CONSTANT PollingEnabled = TRUE
SPECIFICATION TraceSpec
PROPERTY Conforms
EOF
echo "$dir/${name}Trace.tla"
```

- [ ] **Step 2: Gitignore the generated artifacts**

Append to `.gitignore`:

```
# Generated trace-check modules
docs/formal/**/*Trace.tla
docs/formal/**/*Trace.cfg
```

- [ ] **Step 3: Generate and validate against the good sample**

Run:

```bash
chmod +x tools/tla/gen-trace-spec.sh
tools/tla/gen-trace-spec.sh docs/formal/opcua-client/OpcUaClient.tla
cd docs/formal/opcua-client
TRACE_PATH="$PWD/samples/ok.ndjson" ../../../tools/tla/tlc OpcUaClientTrace.tla 2>&1 | tail -6
rm -rf states; cd -
```

Expected: `Conforms` holds, no error. Troubleshooting: if `Items <- TraceItems` is rejected, the deserialized `cover` is not usable as a function domain in this build; use the fallback of a generated `CONSTANT Items = {"n1","n2"}` extracted with `jq`. If `IOEnv.TRACE_PATH` errors, try `getenv("TRACE_PATH")`. If `ndJsonDeserialize` is unknown, the community jar is off the classpath (Task 1).

- [ ] **Step 4: Confirm rejection of the bad sample**

Run:

```bash
cd docs/formal/opcua-client
TRACE_PATH="$PWD/samples/bad.ndjson" ../../../tools/tla/tlc OpcUaClientTrace.tla 2>&1 | tail -6
rm -rf states OpcUaClientTrace.tla OpcUaClientTrace.cfg; cd -
```

Expected: `Conforms` is violated (the trace stalls at `l = 1`). This proves the generated checker has teeth.

- [ ] **Step 5: Commit**

```bash
git add tools/tla/gen-trace-spec.sh .gitignore
git commit -m "Generate the trace-check module from the model's VARIABLES"
```

---

### Task 4: The `check-traces.sh` driver

**Files:**
- Create: `tools/tla/check-traces.sh`

- [ ] **Step 1: Write the driver**

Create `tools/tla/check-traces.sh`:

```bash
#!/usr/bin/env bash
# Validate every behavior in an ND-JSON trace file against a TLA+ model.
# Usage: check-traces.sh <traces.ndjson> <Model.tla>
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
traces="$1"; model="$2"
[ -f "$traces" ] || { echo "no trace file: $traces" >&2; exit 2; }
[ -f "$model" ]  || { echo "no model file: $model" >&2; exit 2; }

spec="$("$here/gen-trace-spec.sh" "$model")"      # generates <Model>Trace.tla(+cfg)
dir="$(dirname "$spec")"; spec_name="$(basename "$spec" .tla)"

fail=0; n=0
while IFS= read -r line; do
  [ -z "$line" ] && continue
  n=$((n + 1))
  one="$(mktemp --suffix=.ndjson)"; printf '%s\n' "$line" > "$one"
  if ! ( cd "$dir" && TRACE_PATH="$one" "$here/tlc" "$spec_name.tla" >/tmp/tlc.out 2>&1; ) || grep -q "is violated" /tmp/tlc.out; then
    echo "NON-CONFORMING behavior #$n:"; tail -20 /tmp/tlc.out; fail=1
  fi
  rm -f "$one"; ( cd "$dir" && rm -rf states )
done < "$traces"

( cd "$dir" && rm -f "$spec_name.tla" "$spec_name.cfg" )
echo "checked $n behavior(s)"; exit $fail
```

- [ ] **Step 2: Run against good, bad, and a mixed file**

Run:

```bash
chmod +x tools/tla/check-traces.sh
tools/tla/check-traces.sh docs/formal/opcua-client/samples/ok.ndjson  docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
tools/tla/check-traces.sh docs/formal/opcua-client/samples/bad.ndjson docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
cat docs/formal/opcua-client/samples/ok.ndjson docs/formal/opcua-client/samples/bad.ndjson > /tmp/mixed.ndjson
tools/tla/check-traces.sh /tmp/mixed.ndjson docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
```

Expected: good `exit: 0`; bad `NON-CONFORMING behavior #1`, `exit: 1`; mixed `checked 2 behavior(s)`, `NON-CONFORMING behavior #2`, `exit: 1`.

- [ ] **Step 3: Commit**

```bash
git add tools/tla/check-traces.sh
git commit -m "Add check-traces.sh driver (generate then validate per behavior)"
```

---

### Task 5: Fix-path teeth samples

**Files:**
- Create: `docs/formal/opcua-client/samples/transfer-fail-ok.ndjson`
- Create: `docs/formal/opcua-client/samples/orphaned-bad.ndjson`

Prove the full-state check catches the #359 regression shapes.

- [ ] **Step 1: Conforming transfer-fail then manual-recreate**

Create `docs/formal/opcua-client/samples/transfer-fail-ok.ndjson` (one line): the states in order `SessionActive` (both Subscribed, linkUp true), `SessionActive` (linkUp false), `ReconnectingSdk` (linkUp false then a state with linkUp true), `Abandoning` (buffering true, both Retrying), `ReconnectingManual` (buffering true), `SessionActive` (buffering false, both Subscribed). Each a full snapshot object.

- [ ] **Step 2: Run it**

```bash
tools/tla/check-traces.sh docs/formal/opcua-client/samples/transfer-fail-ok.ndjson docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
```

Expected: `exit: 0`. If rejected, a hand-written step is illegal (for example buffering not set at `Abandoning`); fix the sample to a real model path.

- [ ] **Step 3: Non-conforming orphaned item (fix-1 shape)**

Create `docs/formal/opcua-client/samples/orphaned-bad.ndjson` (one line): reach `SessionActive` with `cover.n1 = "Orphaned"`, which `NoOrphanedItem` forbids.

```bash
tools/tla/check-traces.sh docs/formal/opcua-client/samples/orphaned-bad.ndjson docs/formal/opcua-client/OpcUaClient.tla; echo "exit: $?"
```

Expected: `NON-CONFORMING behavior #1`, `exit: 1`.

- [ ] **Step 4: Commit**

```bash
git add docs/formal/opcua-client/samples/transfer-fail-ok.ndjson docs/formal/opcua-client/samples/orphaned-bad.ndjson
git commit -m "Add fix-path conformance and fix-1 rejection sample traces"
```

---

### Task 6: Document the pipeline

**Files:**
- Modify: `docs/formal/README.md`

- [ ] **Step 1: Add a trace-validation section**

In `docs/formal/README.md`, after "Running a model", add:

```markdown
## Trace validation (binding code to a model)

Two steps: a run that emits an ND-JSON trace file (one behavior per line), then a
check that validates each behavior against the model.

    tools/tla/check-traces.sh <traces.ndjson> docs/formal/opcua-client/OpcUaClient.tla

The driver generates the trace-check module from the model's VARIABLES
(`gen-trace-spec.sh`), so the model is the single source of truth and there is
nothing to hand-sync. The generated module pins each variable to the recorded
values and requires each step to satisfy `Next`; a behavior that is not legal
makes the `Conforms` property fail and the driver exits non-zero.

Sample traces under `opcua-client/samples/` show conforming and non-conforming
behaviors. The emission side (a `[Conditional("MODELTRACE")]` C# helper and a
test-side sink) is Phase 2b.
```

- [ ] **Step 2: Commit**

```bash
git add docs/formal/README.md
git commit -m "Document the generated trace-validation pipeline"
```

---

## Self-Review

**Spec coverage:** checking mechanism generated from the model (Tasks 3, 4); Community Modules toolchain (Task 1); TLC as sole authority on `Next` (generated module conjoins `Next`); per-trace `Items` (generator's `TraceItems`); teeth including a fix-1 shape (Tasks 3, 4, 5); ND-JSON one behavior per line (Tasks 2, 5); single-source determinism, nothing hand-synced (Task 3). Emission (`[Conditional]` helper, sink, instrumentation, CI) is Phase 2b.

**Placeholder scan:** `<URL>`/`<SHA>` are captured in Task 1 Step 1 before use. The full-state sample contents in Task 5 are specified state-by-state with the snapshot shape from Task 2. No "TBD"/"handle edge cases".

**Consistency:** field names (`state`, `linkUp`, `cover`, `buffering`, `stalled`, `seq`), the `TRACE_PATH` env var, `Conforms`, and the generated `<Model>Trace` naming are consistent across Tasks 2 to 5 and the generator.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-08-opcua-client-trace-validation-phase2a-pipeline.md`.
