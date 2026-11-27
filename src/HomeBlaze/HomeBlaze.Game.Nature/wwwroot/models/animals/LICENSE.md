# Animal models

These models are by Quaternius (https://quaternius.com/) from the "Ultimate Animated Animal Pack" (https://quaternius.com/packs/ultimateanimatedanimals.html), released under CC0 1.0 (https://creativecommons.org/publicdomain/zero/1.0/) as stated on the pack page and on each model page. They were downloaded on 2026-11-01 from the author's uploads on Poly Pizza:

- `deer.glb`: "Deer" by Quaternius, https://poly.pizza/m/T6Cs7tmMHJ (uploaded 2021-09-07, license "CC0 1.0"), original file https://static.poly.pizza/4b6c2a41-43c7-404c-ae37-e8c4645ff93b.glb, SHA-256 `fabf360b22eaccccd056ee99d698a9562d15aa245821f96e4fbad6db3751604e`.
- `stag.glb`: "Stag" by Quaternius, https://poly.pizza/m/tQdzbZ1Cmw (uploaded 2021-09-07, license "CC0 1.0"), original file https://static.poly.pizza/a9c69fbc-bf7c-4585-9a49-a82e0be1ac6b.glb, SHA-256 `3c2df7c13a3d37c8660684e46aa1e5a2bf7e91a9b279983921d48bd93d0b67ed`.

Two changes were made. To keep the files small, only the animations `Idle` (3.33 s), `Walk` (1.17 s) and `Eating` (6 s for the deer, 12.08 s for the stag) were kept and the binary buffer was repacked without the data of the removed animations. The skin's `skeleton` was changed from the `Body` bone to `AnimalArmature`, the common ancestor of all joints, because the IK and foot bones hang beside `Body` and Babylon.js otherwise leaves them without the armature's transform, which stretches the legs into spikes. Meshes, materials and the kept animations are unchanged. Each file is self-contained: vertex-colored materials, no textures, one skinned armature, the animal facing +Z with its feet at the origin.

## CC0 1.0 Universal

The person who associated a work with this deed has dedicated the work to the public domain by waiving all of his or her rights to the work worldwide under copyright law, including all related and neighboring rights, to the extent allowed by law.

You can copy, modify, distribute and perform the work, even for commercial purposes, all without asking permission.

The full legal code is at https://creativecommons.org/publicdomain/zero/1.0/legalcode.
