# WebSocket Pipeline Architecture

Visual guide to how the WebSocket connector synchronizes state between server and clients.

## The Big Picture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                              SERVER                                      │
│                                                                          │
│  ┌───────────────┐     ┌─────────────────┐     ┌──────────────────────┐  │
│  │ Mutation      │     │ Interceptor     │     │ ChangeQueue          │  │
│  │ Engine /      │────▶│ Chain           │────▶│ Processor (CQP)      │  │
│  │ App Code      │     │ (change notif.) │     │ (batch + dedup)      │  │
│  └───────────────┘     └─────────────────┘     └────────┬─────────────┘  │
│                                                         │                │
│                                                         ▼                │
│                              ┌───────────────────────────────────────┐   │
│                              │      WebSocketSubjectHandler          │   │
│                              │                                       │   │
│                              │  ┌─────────────────────────────────┐  │   │
│                              │  │  _applyUpdateLock               │  │   │
│                              │  │  ┌───────────────────────────┐  │  │   │
│                              │  │  │ CreatePartialUpdate       │  │  │   │
│                              │  │  │ Increment _sequence       │  │  │   │
│                              │  │  │ UpdateSentState (all conn)│  │  │   │
│                              │  │  │ ComputeHash (all conn)    │  │  │   │
│                              │  │  └───────────────────────────┘  │  │   │
│                              │  └─────────────────────────────────┘  │   │
│                              │                 │                     │   │
│                              └─────────────────┼─────────────────────┘   │
│                                                │                         │
│         ┌──────────────────────────────────────┼───────────────┐         │
│         │                                      │               │         │
│         ▼                                      ▼               ▼         │
│  ┌──────────────┐                    ┌──────────────┐  ┌──────────────┐  │
│  │ Connection A │                    │ Connection B │  │ Connection C │  │
│  │              │                    │              │  │              │  │
│  │ SentState: ██│                    │ SentState: ██│  │ SentState: ██│  │
│  │ Hash: abc123 │                    │ Hash: abc123 │  │ Hash: def456 │  │
│  │              │                    │              │  │ (just joined)│  │
│  └──────┬───────┘                    └──────┬───────┘  └──────┬───────┘  │
│         │                                   │                 │          │
└─────────┼───────────────────────────────────┼─────────────────┼──────────┘
          │ WebSocket                         │                 │
          ▼                                   ▼                 ▼
┌─────────────────┐                ┌─────────────────┐  ┌─────────────────┐
│   CLIENT A      │                │   CLIENT B      │  │   CLIENT C      │
│                 │                │                 │  │  (reconnecting) │
│ SentState: ██   │                │ SentState: ██   │  │ SentState: ██   │
│ Hash: abc123 ✓  │                │ Hash: abc123 ✓  │  │ Hash: def456 ✓  │
└─────────────────┘                └─────────────────┘  └─────────────────┘
```

## Server Broadcast Pipeline (property change → client receives)

```
Step 1: CAPTURE                Step 2: FILTER + BATCH         Step 3: CREATE UPDATE
─────────────────              ──────────────────────         ────────────────────

 Property write                CQP subscription               SubjectUpdateFactory
 on any thread                 dequeues changes                converts changes
      │                              │                         to SubjectUpdate
      ▼                              ▼                              │
 Interceptor chain             ┌─────────────┐                      ▼
 fires change                  │ Filter:     │               ┌─────────────┐
 notification                  │             │               │ Value props │
      │                        │ Registered? │               │ → value     │
      ▼                        │   YES → ask │               │             │
 Enqueued in                   │   PathProv. │               │ Structural  │
 lock-free queue               │             │               │ props →     │
                               │ Unregistered│               │ complete    │
                               │   → cache   │               │ subjects    │
                               │   hit? YES  │               └─────────────┘
                               │   miss? DROP│
                               └─────────────┘


Step 4: LOCK + SEQUENCE        Step 5: PER-CONNECTION HASH    Step 6: BROADCAST
────────────────────           ──────────────────────────     ─────────────────

 _applyUpdateLock {             For each connection:           For each connection:

   Create SubjectUpdate          UpdateSentState(update)        Serialize with
   from changes                  ─ track structural            per-connection hash
                                   property changes
   _sequence++                   ─ reference counting          Send via WebSocket
                                   for orphan detection
   For each connection:                                        Skip if seq ≤
     update sent state           ComputeHash()                 welcomeSequence
     compute hash                ─ return cached if            (already in Welcome)
                                   not dirty
 }                               ─ SHA256 if dirty
```

## Client Receive Pipeline (server update → applied locally)

```
 WebSocket message received
          │
          ▼
 ┌────────────────────┐
 │ Deserialize        │
 │ UpdatePayload      │
 │ (subjects + hash)  │
 └────────┬───────────┘
          │
          ▼
 ┌────────────────────┐     ┌──────────────────┐
 │ Sequence check     │────▶│ Gap detected?    │──── YES ──▶ RECONNECT
 │ (expected N,       │     │ (expected ≠ got) │
 │  got N? continue)  │     └──────────────────┘
 └────────┬───────────┘
          │ OK
          ▼
 ┌────────────────────┐
 │ Apply update       │     ┌──────────────────────────────────────────┐
 │ (SubjectUpdate     │     │ SubjectUpdateApplier (3-phase):          │
 │  Applier)          │────▶│                                          │
 └────────┬───────────┘     │ 1. Pre-resolve: cache subject IDs        │
          │                 │    before any mutations                  │
          │                 │                                          │
          │                 │ 2. Root path: structural properties      │
          │                 │    processed recursively                 │
          │                 │    ┌─────────────────────────────────┐   │
          │                 │    │ Lifecycle Batch Scope:          │   │
          │                 │    │ Subjects moving between props   │   │
          │                 │    │ stay "attached" throughout      │   │
          │                 │    └─────────────────────────────────┘   │
          │                 │                                          │
          │                 │ 3. Remaining subjects: apply properties  │
          │                 │    + retry unresolved from phase 2       │
          │                 └──────────────────────────────────────────┘
          │
          ▼
 ┌────────────────────┐
 │ Update client      │
 │ SentStructuralState│
 │ from update content│
 └────────┬───────────┘
          │
          ▼
 ┌────────────────────┐     ┌──────────────────┐
 │ Hash comparison    │────▶│ Mismatch?        │──── YES ──▶ RECONNECT
 │ server vs client   │     │ (structural      │
 │ sent-state hash    │     │  divergence)     │
 └────────┬───────────┘     └──────────────────┘
          │ OK
          ▼
        DONE
```

## New Client Connection (Welcome handshake)

```
      CLIENT                                    SERVER
        │                                         │
        │──── Hello (version, format) ───────────▶│
        │                                         │
        │                              ┌──────────┴───────────┐
        │                              │ Register connection  │
        │                              │ in _connections      │
        │                              │ (before Welcome!)    │
        │                              │                      │
        │                              │ _applyUpdateLock {   │
        │                              │   read _sequence     │
        │                              │   create snapshot    │
        │                              │   init SentState     │
        │                              │ }                    │
        │                              └──────────┬───────────┘
        │                                         │
        │◀──── Welcome (snapshot, seq=N) ─────────│
        │                                         │
        │      ┌─ Updates with seq ≤ N            │
        │      │  were queued during              │
        │      │  registration window.            │
        │      │  SendWelcomeAsync skips          │
        │      │  them (seq ≤ welcomeSeq)         │
        │      └──────────────────────            │
        │                                         │
        │  init client SentState                  │
        │  from Welcome snapshot                  │
        │                                         │
        │◀──── Update (seq=N+1, hash) ────────────│
        │◀──── Update (seq=N+2, hash) ────────────│
        │◀──── ...                                │
```

## Safety Layers

```
Layer 0: DELIVERY GUARANTEE
═══════════════════════════

  TCP guarantees ordered, reliable byte delivery for open connections.
  WebSocket frames are delivered in order on top of TCP.

  ┌────────────────────────────────────────────────────────┐
  │ Covers: message delivery, ordering                     │
  │ Doesn't cover: application-level failures,             │
  │   connection drops, silent processing errors           │
  └────────────────────────────────────────────────────────┘


Layer 1: SEQUENCE NUMBERS
═════════════════════════

  Server increments _sequence atomically under _applyUpdateLock.
  Client tracks expected next sequence. Gap → reconnect.

  ┌────────────────────────────────────────────────────────┐
  │ Covers: dropped messages, out-of-order delivery,       │
  │   connection drops                                     │
  │ Doesn't cover: correctly-delivered message where       │
  │   a specific property fails to apply                   │
  └────────────────────────────────────────────────────────┘


Layer 2: STRUCTURAL HASH (sent-state model)
═══════════════════════════════════════════

  Both server and client track structural state from update
  content (not live graph). SHA256 hash compared per update
  and on idle heartbeat. Mismatch → reconnect → Welcome.

  ┌────────────────────────────────────────────────────────┐
  │ Covers: structural divergence (subjects exist on one   │
  │   side but not the other, collection/dict differences) │
  │ Doesn't cover: value-only divergence (same structure,  │
  │   different property values)                           │
  └────────────────────────────────────────────────────────┘


Layer 3: RECONNECTION + WELCOME (full re-sync)
══════════════════════════════════════════════

  On any detected issue (seq gap, hash mismatch, connection
  drop), client reconnects. Server sends Welcome with
  complete current state. Fixes ALL divergence.

  ┌────────────────────────────────────────────────────────┐
  │ Covers: everything — structure + values                │
  │ Cost: full state transfer, brief interruption          │
  │ Trigger: seq gap, hash mismatch, TCP disconnect,       │
  │   receive timeout, circuit breaker                     │
  └────────────────────────────────────────────────────────┘


Layer ?: VALUE HASH (not implemented — follow-up)
═════════════════════════════════════════════════

  Would extend sent-state model to track value state.
  Catches value-only divergence without reconnection
  from another trigger.

  ┌────────────────────────────────────────────────────────┐
  │ Would cover: value divergence that doesn't trigger     │
  │   any other safety layer                               │
  │ Practical risk: near-zero (requires TCP anomaly or     │
  │   undiscovered applier bug)                            │
  └────────────────────────────────────────────────────────┘
```

## CQP Filter (PathProvider cache)

```
  PropertyReference arrives at CQP filter
          │
          ▼
  ┌───────────────────────┐
  │ TryGetRegistered      │
  │ Property()            │
  └───────┬───────────────┘
          │
     ┌────┴────┐
     │         │
  non-null    null
  (registered) (momentarily unregistered)
     │         │
     ▼         ▼
  ┌─────────┐  ┌──────────────────────────┐
  │ Ask     │  │ Check subject.Data cache │
  │ Path    │  │ for (prefix, propName)   │
  │ Provider│  │                          │
  │         │  │ Cache hit?               │
  │ Cache   │  │   YES → use cached       │
  │ result  │  │         decision         │
  │ in Data │  │   NO  → DROP (unknown    │
  │         │  │         property)        │
  │ Eagerly │  └──────────────────────────┘
  │ cache   │
  │ ALL     │  Cache is populated eagerly:
  │ sibling │  first time ANY property of a
  │ props   │  subject goes through filter
  │         │  while registered, ALL sibling
  │         │  properties are cached at once.
  └─────────┘
```

## Lifecycle Batch Scope (subject moves)

```
  WITHOUT batch scope:                WITH batch scope (during ApplyUpdate):

  Dict A has subject X                Dict A has subject X
  Update moves X: A→B                 Update moves X: A→B

  Step 1: Remove X from A             Step 1: Remove X from A
    → lifecycle: last detach!            → lifecycle: last detach?
    → remove from _attachedSubjects      → batch active → DEFER
    → remove from _knownSubjects         → X stays in both maps
    → fire SubjectDetaching              → no cleanup yet
    → CQP: X unregistered! ✗            → CQP: X still registered ✓

  Step 2: Add X to B                  Step 2: Add X to B
    → lifecycle: new attach              → lifecycle: re-attach
    → re-register in both maps           → X already in maps
    → re-fire events                     → just update reference
                                         → no events needed

  Problem: between steps 1-2,         Step 3: End batch scope
  X is unregistered. CQP filter         → check deferred subjects
  drops any X changes. Registry          → X has references → skip
  lookups fail.                          → (genuinely orphaned
                                            subjects get full detach)
```

## Thread Model

```
  ┌─────────────────────────────────────────────────────────────────┐
  │                        SERVER PROCESS                           │
  │                                                                 │
  │  ┌──────────────┐  ┌──────────────┐  ┌───────────────────────┐  │
  │  │ CQP Flush    │  │ Heartbeat    │  │ Client Handler        │  │
  │  │ Thread       │  │ Timer Thread │  │ Threads (1 per conn)  │  │
  │  │              │  │              │  │                       │  │
  │  │ Dequeues     │  │ Periodic     │  │ ReceiveUpdateAsync    │  │
  │  │ changes,     │  │ hash check   │  │ (applies client       │  │
  │  │ creates      │  │ when idle    │  │  updates under        │  │
  │  │ updates,     │  │              │  │  _applyUpdateLock)    │  │
  │  │ broadcasts   │  │              │  │                       │  │
  │  └──────┬───────┘  └──────┬───────┘  └────────────┬──────────┘  │
  │         │                 │                       │             │
  │         └────────┬────────┘                       │             │
  │                  │                                │             │
  │                  ▼                                │             │
  │         ┌────────────────┐                        │             │
  │         │_applyUpdateLock│◀───────────────────────┘             │
  │         │                │                                      │
  │         │ Serializes:    │                                      │
  │         │ • Update create│                                      │
  │         │ • Seq increment│                                      │
  │         │ • SentState    │                                      │
  │         │ • Welcome snap │                                      │
  │         │ • Client apply │                                      │
  │         │ • Heartbeat    │                                      │
  │         └────────────────┘                                      │
  └─────────────────────────────────────────────────────────────────┘
```
