---- MODULE OpcUaClient ----
\* Abstract model of the OPC UA client session and subscription lifecycle.
\* Transitions are extracted from Namotion.Interceptor.OpcUa/Client; invariants
\* are stated independently from OPC UA semantics and the reliability bar.
EXTENDS Naturals

CONSTANT Items          \* set of monitored item ids, e.g. {i1, i2}

VARIABLES
    state,              \* session lifecycle state
    linkUp,             \* adversary: server/link reachable
    subscribed,         \* [Items -> BOOLEAN]: which items are subscribed now
    buffering,          \* updates buffered during a manual reconnect
    stalled             \* reconnect deadline exceeded (Stalled bookkeeping)

vars == << state, linkUp, subscribed, buffering, stalled >>

States == { "Disconnected", "Connecting", "SessionActive",
            "ReconnectingSdk", "ReconnectingManual", "Stalled", "Faulted" }

AllSubscribed   == \A i \in Items : subscribed[i]
NoneSubscribed  == [i \in Items |-> FALSE]
EverySubscribed == [i \in Items |-> TRUE]

TypeOK ==
    /\ state \in States
    /\ linkUp \in BOOLEAN
    /\ subscribed \in [Items -> BOOLEAN]
    /\ buffering \in BOOLEAN
    /\ stalled \in BOOLEAN

Init ==
    /\ state = "Disconnected"
    /\ linkUp = TRUE
    /\ subscribed = NoneSubscribed
    /\ buffering = FALSE
    /\ stalled = FALSE

\* --- Adversary: the link may drop and later recover in any state ---
LinkDrops ==
    /\ linkUp
    /\ linkUp' = FALSE
    /\ UNCHANGED << state, subscribed, buffering, stalled >>

LinkRecovers ==
    /\ ~linkUp
    /\ linkUp' = TRUE
    /\ UNCHANGED << state, subscribed, buffering, stalled >>

\* --- Initial connect and activation (session + create subscriptions) ---
Connect ==
    /\ state = "Disconnected"
    /\ linkUp
    /\ state' = "Connecting"
    /\ UNCHANGED << linkUp, subscribed, buffering, stalled >>

Activate ==
    /\ state = "Connecting"
    /\ linkUp
    /\ state' = "SessionActive"
    /\ subscribed' = EverySubscribed
    /\ UNCHANGED << linkUp, buffering, stalled >>

\* --- SDK keep-alive notices the link is gone ---
KeepAliveDetectsLoss ==
    /\ state = "SessionActive"
    /\ ~linkUp
    /\ state' = "ReconnectingSdk"
    /\ UNCHANGED << linkUp, subscribed, buffering, stalled >>

\* --- SDK auto-reconnect: subscriptions transferred to the new session ---
SdkReconnectTransferOk ==
    /\ state = "ReconnectingSdk"
    /\ linkUp
    /\ state' = "SessionActive"
    /\ subscribed' = EverySubscribed
    /\ UNCHANGED << linkUp, buffering, stalled >>

\* --- SDK auto-reconnect: transfer fails, fall to manual recreate ---
SdkReconnectTransferFails ==
    /\ state = "ReconnectingSdk"
    /\ linkUp
    /\ state' = "ReconnectingManual"
    /\ subscribed' = NoneSubscribed      \* old subscriptions lost
    /\ buffering' = TRUE
    /\ UNCHANGED << linkUp, stalled >>

\* --- Manual reconnect recreates subscriptions from scratch ---
ManualReconnectRecreate ==
    /\ state = "ReconnectingManual"
    /\ linkUp
    /\ state' = "SessionActive"
    /\ subscribed' = EverySubscribed
    /\ buffering' = FALSE
    /\ UNCHANGED << linkUp, stalled >>

\* --- Manual reconnect fails while the link is still down ---
ManualReconnectFails ==
    /\ state = "ReconnectingManual"
    /\ ~linkUp
    /\ state' = "Faulted"
    /\ UNCHANGED << linkUp, subscribed, buffering, stalled >>

\* --- Health check retries after a fault ---
RetryFromFault ==
    /\ state = "Faulted"
    /\ state' = "ReconnectingManual"
    /\ buffering' = TRUE
    /\ UNCHANGED << linkUp, subscribed, stalled >>

\* --- Reconnect exceeded its deadline (link never returned) ---
StallTimeout ==
    /\ state \in { "ReconnectingSdk", "ReconnectingManual" }
    /\ ~linkUp
    /\ state' = "Stalled"
    /\ stalled' = TRUE
    /\ UNCHANGED << linkUp, subscribed, buffering >>

\* --- Force reset drops to a clean manual reconnect ---
ForceReset ==
    /\ state = "Stalled"
    /\ state' = "ReconnectingManual"
    /\ subscribed' = NoneSubscribed
    /\ buffering' = TRUE
    /\ stalled' = FALSE
    /\ UNCHANGED << linkUp >>

Next ==
    \/ LinkDrops \/ LinkRecovers
    \/ Connect \/ Activate
    \/ KeepAliveDetectsLoss
    \/ SdkReconnectTransferOk \/ SdkReconnectTransferFails
    \/ ManualReconnectRecreate \/ ManualReconnectFails \/ RetryFromFault
    \/ StallTimeout \/ ForceReset

Fairness ==
    /\ WF_vars(Connect)
    /\ WF_vars(Activate)
    /\ WF_vars(KeepAliveDetectsLoss)
    /\ WF_vars(SdkReconnectTransferOk)
    /\ WF_vars(ManualReconnectRecreate)
    /\ WF_vars(RetryFromFault)
    /\ WF_vars(ForceReset)

Spec == Init /\ [][Next]_vars /\ Fairness

\* ---------------- Invariants (independent of the code) ----------------
NoOrphanedItem == (state = "SessionActive") => AllSubscribed

BufferingOnlyDuringManualRecovery ==
    buffering => state \in { "ReconnectingManual", "Faulted", "Stalled" }

\* ---------------- Liveness ----------------
Converged == state = "SessionActive" /\ AllSubscribed

\* If the link eventually stays up forever, the client eventually converges and
\* stays converged (re-subscribes after every reconnect, then settles). The
\* weaker <>Converged is satisfied by the first Activate and cannot detect a
\* failure to re-converge after a reconnect; <>[]Converged can.
Liveness == (<>[]linkUp) => <>[]Converged
====
