# People

The six people are from the February 2019 "Animated Men Characters" and "Animated Women Characters" Patreon packs by Quaternius (https://quaternius.com), released under CC0 1.0 (https://creativecommons.org/publicdomain/zero/1.0/). The direct evidence is the `License.txt` in the `FreeModels by Quaternius[Patreon]/` folder of the mirror below, which dedicates the models of that folder to the public domain under CC0 1.0 (https://github.com/beep2bleep/FreeAssetsByKenneyNLandQuaternius/blob/dea756baf3b3a4889d8c245e456a4791f961578a/FreeModels%20by%20Quaternius%5BPatreon%5D/License.txt); the Quaternius FAQ (https://quaternius.com/faq.html) states the same for all Quaternius models.

The source files are the FBX models mirrored at https://github.com/beep2bleep/FreeAssetsByKenneyNLandQuaternius/tree/dea756baf3b3a4889d8c245e456a4791f961578a, under `FreeModels by Quaternius[Patreon]/Characters and Animals/Animated Men Characters - Feb 2019/FBX/` and `.../Animated Women Characters - Feb 2019/FBX/`:

| File | Source |
|---|---|
| `male-casual` | `Male_Casual.fbx` |
| `male-shirt` | `Male_Shirt.fbx` |
| `male-long-sleeve` | `Male_LongSleeve.fbx` |
| `female-casual` | `Female_Casual.fbx` |
| `female-alternative` | `Female_Alternative.fbx` |
| `female-tank-top` | `Female_TankTop.fbx` |

They were converted with `HomeBlaze.Game/tools/convert-people.js` and three.js 0.160.0: every material became a matte one of the same name and color, only the clips `Idle`, `Walk`, `Clapping`, `Sitting` and `Jump` were kept under these names, the unused texture coordinates were dropped, the geometry was welded and sorted by material, the skin weights were stored as bytes, and the animation keys that change nothing were dropped. Each `.png` is a preview of the model in the first frame of `Idle`, rendered by the same script.
