---- MODULE JsonSmoke ----
\* Self-test: verify the Community Modules Json module is on the classpath.
\* ToJson([a |-> 1]) should produce a non-empty JSON string.
EXTENDS Json

VARIABLE dummy

Init == dummy = 0
Next == dummy' = dummy

JsonInv == ToJson([a |-> 1]) # ""

Spec == Init /\ [][Next]_dummy

====
