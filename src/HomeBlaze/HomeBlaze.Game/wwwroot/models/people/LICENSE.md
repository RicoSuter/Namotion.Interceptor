# People

The six people are the animated men and women characters of February 2019 by Quaternius, published as the Ultimate Animated Character Pack (https://quaternius.com/packs/ultimatedanimatedcharacter.html) and released under CC0 1.0 (https://creativecommons.org/publicdomain/zero/1.0/) as stated on that page and in the Quaternius FAQ (https://quaternius.com/faq.html).

The source files are the FBX models mirrored at https://github.com/beep2bleep/FreeAssetsByKenneyNLandQuaternius/tree/dea756baf3b3a4889d8c245e456a4791f961578a, under `FreeModels by Quaternius[Patreon]/Characters and Animals/Animated Men Characters - Feb 2019/FBX/` and `.../Animated Women Characters - Feb 2019/FBX/`:

| File | Source |
|---|---|
| `male-casual` | `Male_Casual.fbx` |
| `male-shirt` | `Male_Shirt.fbx` |
| `male-long-sleeve` | `Male_LongSleeve.fbx` |
| `female-casual` | `Female_Casual.fbx` |
| `female-alternative` | `Female_Alternative.fbx` |
| `female-tank-top` | `Female_TankTop.fbx` |

They were converted with `HomeBlaze.Game/tools/convert-people.js` and three.js 0.160.0: every material became a matte one of the same name and color, only the clips `Idle`, `Walk`, `Clapping`, `Sitting` and `Jump` were kept under these names, the geometry was welded and sorted by material, the skin weights were stored as bytes, and the animation keys that change nothing were dropped. Each `.png` is a preview of the model in the first frame of `Idle`, rendered by the same script.
