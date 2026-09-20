# Core Context: Architecture Dossier

Area: `src/Namotion.Interceptor/` (the core library, .NET Standard 2.0). 36 files, 3544 lines.
Boundary: Tracking, Registry, Connectors and the source generator are out of scope and get their own dossiers. Where core defines a contract those libraries depend on, the contract is in scope and the consumer's use of it is not.
Written against: `33ed5447f7dd095f69906b301fbf5154c2672559`
Verified: pending

## 1. Supported use cases

| # | Use case | Status | Cost | Ruling |
|---|---|---|---|---|
| | | | | |

Status meanings: Supported means guaranteed and defended. Best effort means it usually works and is not guaranteed. Unsupported means explicitly ruled out. Undecided means nobody has ruled, which is not the same as supported.

## 2. Contracts and invariants

| # | Invariant | Evidence | Covered |
|---|---|---|---|
| | | | |

## 3. Concept census

## 4. Flows

## 5. Threading and shared state

| State | Owner | Protected by | Ordering guarantee | Read without the lock |
|---|---|---|---|---|
| | | | | |

## 6. Gaps and limitations

| # | Gap | Kind | Issue |
|---|---|---|---|
| | | | |

## 7. Candidates

## 8. Backlog disposition

| Issue / PR | Disposition | Rationale |
|---|---|---|
| | | |
