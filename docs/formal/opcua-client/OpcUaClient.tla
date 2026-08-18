---- MODULE OpcUaClient ----
\* Abstract model of the OPC UA client session and subscription lifecycle.
\* Transitions are extracted from Namotion.Interceptor.OpcUa/Client; invariants
\* are stated independently from OPC UA semantics and the reliability bar.
\*
\* Models the two correctness fixes from PR #359 (both surfaced by this
\* formalization) and makes the invariants fail under the pre-fix behavior:
\*   1. A transiently-failed monitored item is kept for retry (and escalated to
\*      polling if it keeps failing), never dropped and orphaned.
\*   2. Notifications are buffered from session abandon, so no stale value from
\*      the abandoned subscription is applied during the reconnect gap.
EXTENDS Naturals

CONSTANT Items,          \* set of monitored item ids, e.g. {i1, i2}
         PollingEnabled  \* whether polling fallback is available (default TRUE)

VARIABLES
    state,               \* session lifecycle state
    linkUp,              \* adversary: server/link reachable
    cover,               \* [Items -> Coverage]: per-item delivery coverage
    buffering,           \* updates buffered during a manual reconnect / abandon
    stalled              \* reconnect deadline exceeded (Stalled bookkeeping)

vars == << state, linkUp, cover, buffering, stalled >>

States == { "Disconnected", "Connecting", "SessionActive",
            "ReconnectingSdk", "Abandoning", "ReconnectingManual",
            "Stalled", "Faulted" }

\* Subscribed/Polling deliver values; Retrying is a transient failure kept in the
\* subscription for the health monitor; Orphaned is a lost item (the fix-1 bug).
Coverage == { "Subscribed", "Retrying", "Polling", "Orphaned" }

ManualRecoveryStates == { "Abandoning", "ReconnectingManual", "Faulted", "Stalled" }

Covered(i)  == cover[i] \in { "Subscribed", "Polling" }
AllCovered  == \A i \in Items : Covered(i)
AllRetrying == [i \in Items |-> "Retrying"]

\* Re-establishing subscriptions: each item either subscribes or transiently
\* fails and is kept for retry. This is fix 1: transient failures are never
\* dropped. The mutation for the teeth check sends them to "Orphaned" instead.
RecreateOutcomes == [Items -> { "Subscribed", "Retrying" }]

TypeOK ==
    /\ state \in States
    /\ linkUp \in BOOLEAN
    /\ cover \in [Items -> Coverage]
    /\ buffering \in BOOLEAN
    /\ stalled \in BOOLEAN

Init ==
    /\ state = "Disconnected"
    /\ linkUp = TRUE
    /\ cover = AllRetrying
    /\ buffering = FALSE
    /\ stalled = FALSE

\* --- Adversary: the link may drop and later recover in any state ---
LinkDrops ==
    /\ linkUp
    /\ linkUp' = FALSE
    /\ UNCHANGED << state, cover, buffering, stalled >>

LinkRecovers ==
    /\ ~linkUp
    /\ linkUp' = TRUE
    /\ UNCHANGED << state, cover, buffering, stalled >>

\* --- Initial connect and activation (session + create subscriptions) ---
Connect ==
    /\ state = "Disconnected"
    /\ linkUp
    /\ state' = "Connecting"
    /\ UNCHANGED << linkUp, cover, buffering, stalled >>

Activate ==
    /\ state = "Connecting"
    /\ linkUp
    /\ state' = "SessionActive"
    /\ cover' \in RecreateOutcomes
    /\ UNCHANGED << linkUp, buffering, stalled >>

\* --- SDK keep-alive notices the link is gone ---
KeepAliveDetectsLoss ==
    /\ state = "SessionActive"
    /\ ~linkUp
    /\ state' = "ReconnectingSdk"
    /\ UNCHANGED << linkUp, cover, buffering, stalled >>

\* --- SDK auto-reconnect: subscriptions transferred to the new session ---
SdkReconnectTransferOk ==
    /\ state = "ReconnectingSdk"
    /\ linkUp
    /\ state' = "SessionActive"
    /\ cover' \in RecreateOutcomes
    /\ UNCHANGED << linkUp, buffering, stalled >>

\* --- SDK auto-reconnect: transfer fails, abandon the session (fix 2: buffer
\* from abandon, because the old subscription callback stays attached) ---
SdkReconnectTransferFails ==
    /\ state = "ReconnectingSdk"
    /\ linkUp
    /\ state' = "Abandoning"
    /\ buffering' = TRUE
    /\ cover' = AllRetrying          \* subscriptions lost, pending re-establish
    /\ UNCHANGED << linkUp, stalled >>

\* --- Enter manual reconnect (buffering stays on across the whole window) ---
BeginManualReconnect ==
    /\ state = "Abandoning"
    /\ state' = "ReconnectingManual"
    /\ UNCHANGED << linkUp, cover, buffering, stalled >>

\* --- Manual reconnect recreates subscriptions and replays buffered updates ---
ManualReconnectRecreate ==
    /\ state = "ReconnectingManual"
    /\ linkUp
    /\ state' = "SessionActive"
    /\ cover' \in RecreateOutcomes
    /\ buffering' = FALSE
    /\ UNCHANGED << linkUp, stalled >>

\* --- Manual reconnect fails while the link is still down ---
ManualReconnectFails ==
    /\ state = "ReconnectingManual"
    /\ ~linkUp
    /\ state' = "Faulted"
    /\ UNCHANGED << linkUp, cover, buffering, stalled >>

\* --- Health check retries after a fault (buffering stays on) ---
RetryFromFault ==
    /\ state = "Faulted"
    /\ state' = "ReconnectingManual"
    /\ UNCHANGED << linkUp, cover, buffering, stalled >>

\* --- Reconnect exceeded its deadline (link never returned) ---
StallTimeout ==
    /\ state \in { "ReconnectingSdk", "ReconnectingManual" }
    /\ ~linkUp
    /\ state' = "Stalled"
    /\ stalled' = TRUE
    /\ UNCHANGED << linkUp, cover, buffering >>

\* --- Force reset drops to a clean manual reconnect ---
ForceReset ==
    /\ state = "Stalled"
    /\ state' = "ReconnectingManual"
    /\ cover' = AllRetrying
    /\ buffering' = TRUE
    /\ stalled' = FALSE
    /\ UNCHANGED << linkUp >>

\* --- Health monitor heals a transiently-failed item (node recovered) ---
HealItem ==
    /\ state = "SessionActive"
    /\ \E i \in Items :
         /\ cover[i] = "Retrying"
         /\ cover' = [cover EXCEPT ![i] = "Subscribed"]
    /\ UNCHANGED << state, linkUp, buffering, stalled >>

\* --- Health monitor escalates a persistently-failing item to polling (fix 1) ---
EscalateItem ==
    /\ state = "SessionActive"
    /\ PollingEnabled
    /\ \E i \in Items :
         /\ cover[i] = "Retrying"
         /\ cover' = [cover EXCEPT ![i] = "Polling"]
    /\ UNCHANGED << state, linkUp, buffering, stalled >>

Next ==
    \/ LinkDrops \/ LinkRecovers
    \/ Connect \/ Activate
    \/ KeepAliveDetectsLoss
    \/ SdkReconnectTransferOk \/ SdkReconnectTransferFails
    \/ BeginManualReconnect
    \/ ManualReconnectRecreate \/ ManualReconnectFails \/ RetryFromFault
    \/ StallTimeout \/ ForceReset
    \/ HealItem \/ EscalateItem

Fairness ==
    /\ WF_vars(Connect)
    /\ WF_vars(Activate)
    /\ WF_vars(KeepAliveDetectsLoss)
    /\ WF_vars(SdkReconnectTransferOk)
    /\ WF_vars(BeginManualReconnect)
    /\ WF_vars(ManualReconnectRecreate)
    /\ WF_vars(RetryFromFault)
    /\ WF_vars(ForceReset)
    /\ WF_vars(EscalateItem)

Spec == Init /\ [][Next]_vars /\ Fairness

\* ---------------- Invariants (independent of the code) ----------------
\* Fix 1: no retryable item is ever dropped and left dark once active.
NoOrphanedItem == (state = "SessionActive") => \A i \in Items : cover[i] /= "Orphaned"

\* Buffering is only ever on inside the manual-recovery window.
BufferingOnlyDuringManualRecovery == buffering => state \in ManualRecoveryStates

\* Fix 2: buffering is on from the moment the session is abandoned.
BufferingCoversAbandon == (state = "Abandoning") => buffering

\* ---------------- Liveness ----------------
\* If the link eventually stays up, the client eventually converges and stays
\* converged: every item covered (subscribed or escalated to polling), and it
\* stays that way. The weaker <>Converged is satisfied by the first Activate and
\* cannot detect a failure to re-converge after a reconnect; <>[]Converged can.
Converged == state = "SessionActive" /\ AllCovered
Liveness == (<>[]linkUp) => <>[]Converged
====
