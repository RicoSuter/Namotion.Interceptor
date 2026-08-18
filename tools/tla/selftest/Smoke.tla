---- MODULE Smoke ----
\* Toolchain self-test: a 3-state counter with a holding invariant.
\* Proves bootstrap + tlc can model-check end to end. Not part of any model.
EXTENDS Naturals

VARIABLE x

Init == x = 0
Next == x' = (x + 1) % 3
Spec == Init /\ [][Next]_x

Inv == x < 3

====
