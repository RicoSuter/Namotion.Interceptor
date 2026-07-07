# Formal models

Machine-checked TLA+ models of concurrency- and failure-sensitive parts of this
repo, plus the process for authoring and maintaining them. Run with the rootless
toolchain in `tools/tla/` (see `tools/tla/README.md`).

## Two verification modes

1. **Model-check the design (TLC).** An abstract model (state variables and
   allowed transitions) plus correctness properties. TLC enumerates every
   reachable state within bounds and returns a counterexample on violation.
   Finds design bugs; needs no code and no tests.
2. **Trace-validate the code.** Instrument the implementation to emit a trace of
   abstract state transitions, run the test suite to generate traces, and check
   each trace is a legal behavior of the model. Binds code to the checked design,
   but only on executions the tests exercise.

They are complementary: model-checking is exhaustive over the model; trace
validation confirms the code matches the model on sampled runs.

## The rule that keeps it honest

Extract the transitions from the code (faithfully, including its warts), but
write the invariants independently (from requirements, never from the code). An
invariant derived from the code only confirms "the code does what the code does".

## Models

- `opcua-client/` — OPC UA client session and subscription lifecycle.

## Running a model

From the repository root, one-time toolchain setup then run a model:

```bash
tools/tla/bootstrap.sh
tools/tla/check-opcua.sh   # OPC UA client lifecycle
```

Any model runs directly with the wrapper:

```bash
( cd docs/formal/<model-dir> && ../../../tools/tla/tlc <Module>.tla )
```

## Reading a counterexample

On a violation TLC prints `Error: Invariant <name> is violated.` (or
`Error: Temporal properties were violated.`) followed by a numbered sequence of
states from the initial state to the violating one. Read it as: perform these
actions in this order and the property breaks at the last state. To confirm an
invariant or property is meaningful, weaken a guard on purpose and check TLC
produces a counterexample (a mutation check). Doing this to the OPC UA liveness
property is what caught that `<>Converged` was too weak and `<>[]Converged` was
the property actually wanted.

## The per-iteration loop

1. Extend the model with the next concern.
2. Model-check with TLC first; fix design bugs before instrumenting.
3. (Trace validation, once wired) extend the instrumentation.
4. Re-run the checks until green.
5. Commit model and instrumentation together.
