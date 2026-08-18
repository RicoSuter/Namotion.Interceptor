# TLA+ toolchain (rootless)

Self-contained TLA+ setup: no Docker, no sudo. `bootstrap.sh` downloads a pinned
`tla2tools.jar` and, only when no system Java is present, a pinned portable
Temurin JRE into `.cache/` (gitignored). `tlc` runs the model checker against
that toolchain, preferring a system Java (for example CI `actions/setup-java`)
and falling back to the portable JRE.

```bash
tools/tla/bootstrap.sh                 # one-time (idempotent) setup
tools/tla/tlc selftest/Smoke.tla       # self-test: expect "No error has been found"
```

The formal models and the modeling process live under `docs/formal/`
(created during the OPC UA client formal-model pilot).
