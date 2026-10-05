---
title: Upgrade and Migration
navTitle: Upgrade
status: Planned
---

# Upgrade and Migration Design [Planned]

## Overview

HomeBlaze deployments need to be upgradeable without data loss. This covers upgrading instances (single-node, HA pairs, multi-instance), migrating configuration when subject types evolve, and ensuring backward compatibility across versions.

**This building block is planned but not yet implemented.**

## Upgrade Scenarios

### Single Instance

Stop the instance, deploy the new binary, start. On startup:
1. Configuration files are loaded — JSON deserialization applies defaults for new properties, ignores removed properties
2. Connectors reconnect to devices, live state recovers
3. History sink reopens its database

Downtime equals restart time. No coordination needed.

### HA Pair (Primary + Standby)

Rolling upgrade — upgrade one node at a time:

1. Upgrade the standby first (it's not serving traffic)
2. Verify the standby starts correctly with the new binary and can read existing configuration
3. Promote the standby to primary (manual or via planned failover)
4. Upgrade the old primary (now standby)

**Requirement:** the new binary must be able to read configuration written by the old binary (backward-compatible deserialization). The old binary does NOT need to read configuration written by the new binary — upgrades are forward-only.

**Requirement:** during the rolling upgrade, both old and new binaries must speak the same WebSocket protocol version. Until wire protocol versioning is implemented (see [Versioning](versioning.md)), both nodes in an HA pair must run the same protocol-compatible version.

### Multi-Instance (Satellites + Central)

Upgrade order matters:

1. Upgrade central first — it receives SubjectUpdate messages from satellites, so it must understand whatever format satellites send
2. Upgrade satellites — they send SubjectUpdate messages to the already-upgraded central

If a new version adds fields to the SubjectUpdate format, the upgraded central must tolerate messages without those fields (from not-yet-upgraded satellites). This is additive compatibility — new fields are optional, old fields keep their meaning.

**Alternative:** if a breaking protocol change is unavoidable, all nodes must be upgraded together (coordinated downtime). This should be rare and documented in release notes.

## Configuration Migration

### The Problem

When a subject type evolves across versions — a `[Configuration]` property is added, renamed, removed, or changes type — the persisted JSON has the old shape. The system must handle this gracefully.

### Current Behavior

`System.Text.Json` deserialization provides basic tolerance:
- **New properties** — deserialized as default values (null, 0, false)
- **Removed properties** — ignored in JSON (no error)
- **Type changes** — may fail deserialization depending on the change

This is sufficient for additive changes (new optional properties) but not for renames, type changes, or semantic migrations.

### Planned: Explicit Migration Logic

For non-trivial changes, subject types need explicit migration:

**Requirements:**
- Each configuration format has a version (stored in the JSON file)
- Migration logic transforms old format to new format before deserialization
- Migrations are chained (v1→v2→v3) so any old version can be upgraded to current
- Migration runs once on startup, migrated files are written back to storage
- Original files are backed up before migration (or the storage backend provides versioning)

**HA constraint:** during rolling upgrades, the standby (new binary) and primary (old binary) may both access the same storage backend (see [Storage](storage.md)). The new binary must be able to read old-format configuration. It should NOT write migrated configuration until the old binary is no longer running — otherwise the old binary may fail to read the migrated format.

**Possible approach:** migrate in memory on load, but defer writing migrated files to storage until explicitly triggered (e.g., after confirming both nodes are on the new version).

### Plugin Configuration Migration

Plugins define their own subject types and therefore their own configuration shapes. Plugin authors need a migration mechanism for their types. This should use the same infrastructure as core configuration migration.

## Wire Protocol Compatibility

See [Versioning](versioning.md) for wire protocol versioning strategy. The key requirement for upgrades:

- Additive changes (new optional fields in SubjectUpdate) must be backward-compatible
- Breaking changes require coordinated upgrades (all nodes at once)
- The Hello/Welcome handshake is prepared for version negotiation but no checking is implemented yet

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| HA upgrade order | Standby first, then promote, then upgrade old primary | Minimizes risk — verify new binary works before it takes traffic |
| Multi-instance upgrade order | Central first, then satellites | Central must tolerate old-format messages from not-yet-upgraded satellites |
| Configuration compatibility | New binary reads old format (backward-compatible) | Enables rolling upgrades without coordinated downtime |
| Migration timing | Migrate in memory on load, defer write-back | Prevents breaking the old binary during rolling upgrades |

## Open Questions

- Configuration version tracking mechanism (field in JSON root? separate metadata file?)
- Migration framework API for plugin authors
- Automated migration testing (verify old configs load correctly with new binary)
- How to handle failed migrations (refuse to start? start with defaults? alert operator?)
- Coordinated upgrade signaling for HA pairs (how does the new primary know the old node is fully shut down before writing migrated config?)
