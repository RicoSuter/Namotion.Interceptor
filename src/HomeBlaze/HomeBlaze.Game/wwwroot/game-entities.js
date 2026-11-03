// Draws the objects of the view. Every object but a player is built from the parts of its visual description: boxes with
// shared materials and glTF models, each loaded once per URL. Players are animated people. World coordinates are relative
// to an integer origin near the own player, so the GPU's 32-bit floats stay precise anywhere in the int range. One unit
// is one tile of 25 cm.
const REBASE_DISTANCE = 2048;
const TELEPORT_DISTANCE = 6;
// One meter, the size of one grass repeat and the spacing of the ground grid.
const METER = 4;
const FADED_VISIBILITY = 0.3;
// A fade glides between fully visible and faded over this time.
const FADE_MS = 200;
// The sightline to the own player's head is tested this often, exactly against the triangles of the meshes it crosses the
// bounds of, and an object it hit stays faded this long after the last hit, so it does not blink while the camera follows
// a step.
const SIGHTLINE_CHECK_MS = 100;
const FADE_HOLD_MS = 300;
const HEAD_HEIGHT = 0.8;
// Keeps a player walking for this part of a step after its move ended, so the next step does not flicker to idle.
const IDLE_DELAY_STEPS = 0.75;
// Lets the planted foot of the 1.04 s walk cycle, which covers about 5.3 tiles scaled to a player's height of 7 tiles,
// keep pace with one tile per step interval, about 1.4 m/s.
const WALKING_SPEED_RATIO = 1.1;
// The names in the people models; the shirt takes the player's color. A person whose visible top is another material
// names it here.
const IDLE_ANIMATION = 'Idle';
const WALKING_ANIMATION = 'Walk';
const SHIRT_MATERIAL = 'Shirt';
const SHIRT_MATERIALS = { 'female-alternative': 'Jacket' };
// A part glides this long to a new offset or rotation, such as a door leaf that swings open.
const GLIDE_MS = 300;
const RADIANS_PER_DEGREE = Math.PI / 180;
// The color of a box without texture or color, and of the box that replaces a model that failed to load.
const FALLBACK_COLOR = '#8d8577';
// The animation mode of a part that plays its animation once; any other mode, or none, loops it.
const ONCE_ANIMATION_MODE = 1;
// Wind bends a part by this many tiles per square tile of height above the object's base, swaying at the first speed and
// gusting at the second, both in radians per second.
const WIND_STRENGTH = 0.0015;
const WIND_SPEED = 1.7;
const GUST_SPEED = 0.37;
const TWO_PI = Math.PI * 2;
// Runs after the vertex is in world space; finalWorld[3] is the mesh's world position, which spreads the phase so that
// neighboring trees do not sway in step.
const WIND_SHADER = `
#ifdef GAME_WIND
    float gameWindHeight = max(worldPos.y - finalWorld[3].y, 0.0);
    float gameWindPhase = finalWorld[3].x * 0.13 + finalWorld[3].z * 0.07;
    float gameWindSway = sin(gameWindSwayPhase + gameWindPhase) * 0.7 + sin(gameWindGustPhase + gameWindPhase * 0.5) * 0.3;
    float gameWindBend = gameWindHeight * gameWindHeight * ${toGlsl(WIND_STRENGTH)} * gameWindSway;
    worldPos.x += gameWindBend;
    worldPos.z += gameWindBend * 0.4;
#endif
`;

const WIND_UNIFORMS = '#ifdef GAME_WIND\nuniform float gameWindSwayPhase;\nuniform float gameWindGustPhase;\n#endif\n';
// Where the wind goes into the vertex shader of the geometry buffer, which draws the outlines and the corner shading:
// its declarations, the world position and the clip position, which that shader computes without the world position.
const GEOMETRY_DEFINITIONS = '#define CUSTOM_VERTEX_DEFINITIONS';
const GEOMETRY_WORLD_POSITION = 'vec4 worldPos=vec4(finalWorld*vec4(positionUpdated,1.0));';
const GEOMETRY_CLIP_POSITION = 'gl_Position=viewProjection*finalWorld*vec4(positionUpdated,1.0);';

// The phases of the sway and the gust in radians, which the wind shader reads; the views of a tab share them. They wrap
// on the CPU, because a 32-bit float of the raw clock loses precision after hours.
let windSwayPhase = 0;
let windGustPhase = 0;
let WindPlugin = null;
// Null until the first wind effect of the geometry buffer, then whether its shader took the wind.
let isGeometryShaderPatched = null;

function toGlsl(value) {
    return value.toFixed(6);
}

// Defined on first use, because Babylon is loaded after this module.
function getWindPlugin() {
    const BABYLON = window.BABYLON;
    WindPlugin ??= class extends BABYLON.MaterialPluginBase {
        constructor(material) {
            super(material, 'GameWind', 200, { GAME_WIND: false });
            this._enable(true);
        }

        prepareDefines(defines) {
            defines.GAME_WIND = true;
        }

        getClassName() {
            return 'GameWindPluginMaterial';
        }

        getUniforms() {
            return {
                ubo: [
                    { name: 'gameWindSwayPhase', size: 1, type: 'float' },
                    { name: 'gameWindGustPhase', size: 1, type: 'float' },
                ],
                vertex: WIND_UNIFORMS,
            };
        }

        bindForSubMesh(uniformBuffer) {
            uniformBuffer.updateFloat('gameWindSwayPhase', windSwayPhase);
            uniformBuffer.updateFloat('gameWindGustPhase', windGustPhase);
        }

        getCustomCode(shaderType) {
            return shaderType === 'vertex' ? { CUSTOM_VERTEX_UPDATE_WORLDPOS: WIND_SHADER } : null;
        }
    };
    return WindPlugin;
}

// Adds the wind to the shader source of the geometry buffer behind the GAME_WIND define; returns whether the source had
// every place the wind goes into, which a Babylon update may change.
function patchGeometryShader() {
    if (isGeometryShaderPatched !== null) {
        return isGeometryShaderPatched;
    }

    const store = window.BABYLON.Effect.ShadersStore;
    const source = store.geometryVertexShader;
    const places = [GEOMETRY_DEFINITIONS, GEOMETRY_WORLD_POSITION, GEOMETRY_CLIP_POSITION];
    if (!source || places.some(place => source.split(place).length !== 2)) {
        console.warn('The geometry buffer shader of this Babylon version has no place for the wind, so outlines do not sway.');
        isGeometryShaderPatched = false;
        return false;
    }

    store.geometryVertexShader = source
        .replace(GEOMETRY_DEFINITIONS, WIND_UNIFORMS + GEOMETRY_DEFINITIONS)
        .replace(GEOMETRY_WORLD_POSITION, GEOMETRY_WORLD_POSITION + WIND_SHADER)
        .replace(GEOMETRY_CLIP_POSITION, `#ifdef GAME_WIND\ngl_Position=viewProjection*worldPos;\n#else\n${GEOMETRY_CLIP_POSITION}\n#endif\n`);
    isGeometryShaderPatched = true;
    return true;
}

// The geometry buffer draws with a shader of its own, which the wind plugin does not reach, so the outline and the corner
// shading of a swaying tree would stay where it stands still. While the buffer prepares a submesh of a wind material, the
// effect it creates gets the GAME_WIND define and the phases. The define is not in the defines the buffer compares, so a
// submesh keeps its first geometry effect: a mesh must never switch between a wind and a non-wind material.
function addWindToGeometryBuffer(geometry, engine) {
    let isPreparingWind = false;
    const isReady = geometry.isReady;
    geometry.isReady = function (subMesh, useInstances) {
        isPreparingWind = Boolean(subMesh.getMaterial()?.gameWind);
        try {
            return isReady.call(this, subMesh, useInstances);
        } finally {
            isPreparingWind = false;
        }
    };

    const createEffect = engine.createEffect;
    engine.createEffect = function (baseName, options, ...rest) {
        if (!isPreparingWind || baseName !== 'geometry' || typeof options?.defines !== 'string' || !patchGeometryShader()) {
            return createEffect.call(this, baseName, options, ...rest);
        }

        const windOptions = {
            ...options,
            defines: `${options.defines}\n#define GAME_WIND`,
            uniformsNames: [...options.uniformsNames, 'gameWindSwayPhase', 'gameWindGustPhase'],
        };
        // The engine returns its cached effect for the same defines, so each effect gets the phases bound once.
        const effect = createEffect.call(this, baseName, windOptions, ...rest);
        if (!effect.gameWind) {
            effect.gameWind = true;
            effect.onBindObservable.add(bindWind);
        }

        return effect;
    };
}

function bindWind(effect) {
    effect.setFloat('gameWindSwayPhase', windSwayPhase);
    effect.setFloat('gameWindGustPhase', windGustPhase);
}

export async function createEntities(scene, options) {
    const BABYLON = window.BABYLON;
    const entities = new Map();
    const targetMarkers = [];
    // Shared by the objects of the view: box materials by texture and color, model loads by URL, the wind variant of each
    // model material, and the faded variant of each material.
    const boxMaterials = new Map();
    const models = new Map();
    const windMaterials = new Map();
    const fadeMaterials = new Map();
    // One container per person, loaded on first use and kept for the view's lifetime.
    const people = new Map();
    // The model URL and animation name of each missing animation that was logged, so it is logged once.
    const missingAnimations = new Set();
    // Written to the canvas for tests and diagnosis.
    const stats = { parts: 0, models: 0, failures: 0, faded: 0, animationsOnce: 0, emotes: 0 };
    // Reused by every frame, so the render loop does not allocate.
    const selfPosition = { x: 0, z: 0 };
    const segment = { enter: 0, exit: 1 };
    const idleDelay = options.stepIntervalMs * IDLE_DELAY_STEPS;
    let originX = null;
    let originY = null;
    let selfId = null;
    // Set by dispose, so that a model load finishing later does not touch the disposed scene.
    let isViewDisposed = false;
    let lastFadeFrame = 0;
    let lastSightlineCheck = -Infinity;
    // Counts the renders; a mesh's world matrix and bounds are computed when the scene renders.
    let frame = 0;
    const sightline = new BABYLON.Ray(new BABYLON.Vector3(), new BABYLON.Vector3(0, 0, 1), 1);
    addWindToGeometryBuffer(options.renderer.geometryBuffer, scene.getEngine());

    const ground = BABYLON.MeshBuilder.CreateGround('ground', { width: 600, height: 600 }, scene);
    ground.material = createGroundMaterial();
    ground.isPickable = false;
    options.renderer.addGround(ground);

    const targetMaterial = new BABYLON.StandardMaterial('target', scene);
    targetMaterial.emissiveColor = new BABYLON.Color3(1, 0.88, 0.4);
    targetMaterial.disableLighting = true;
    targetMaterial.alpha = 0.4;

    updateStats();

    function createGroundMaterial() {
        // The border of each repeat in the texture draws the meter grid.
        const texture = new BABYLON.Texture(`${options.baseUrl}textures/grass.png`, scene);
        texture.anisotropicFilteringLevel = 8;
        texture.uScale = 600 / METER;
        texture.vScale = 600 / METER;
        const material = new BABYLON.StandardMaterial('ground', scene);
        material.diffuseTexture = texture;
        material.specularColor = BABYLON.Color3.Black();
        return material;
    }

    function updateStats() {
        const element = options.statsElement;
        if (element) {
            element.dataset.gameParts = String(stats.parts);
            element.dataset.gameModels = String(stats.models);
            element.dataset.gameModelFailures = String(stats.failures);
            element.dataset.gameFaded = String(stats.faded);
            element.dataset.gameAnimationsOnce = String(stats.animationsOnce);
            element.dataset.gameEmotes = String(stats.emotes);
        }
    }

    function ensureOrigin(x, y) {
        if (originX === null || Math.abs(x - originX) > REBASE_DISTANCE || Math.abs(y - originY) > REBASE_DISTANCE) {
            originX = x;
            originY = y;
            for (const entity of entities.values()) {
                place(entity, entity.view.x, entity.view.y);
            }
        }
    }

    // Players stand on the center of their footprint; the parts of other objects start at its south-west corner.
    function positionOf(view, x, y) {
        return view.kind === 'player'
            ? new BABYLON.Vector3(x - originX + view.width / 2, 0, y - originY + view.depth / 2)
            : new BABYLON.Vector3(x - originX, 0, y - originY);
    }

    function getBoxMaterial(texture, color) {
        const key = `${texture ?? ''}|${color ?? ''}`;
        let material = boxMaterials.get(key);
        if (!material) {
            material = new BABYLON.StandardMaterial(`box-${boxMaterials.size}`, scene);
            if (texture) {
                material.diffuseTexture = new BABYLON.Texture(texture, scene);
            }

            // The diffuse color tints the texture, so a textured box without a color stays white.
            material.diffuseColor = BABYLON.Color3.FromHexString(color ?? (texture ? '#ffffff' : FALLBACK_COLOR));
            material.specularColor = new BABYLON.Color3(0.04, 0.04, 0.04);
            boxMaterials.set(key, material);
        }

        return material;
    }

    // The UVs follow the size of each face, so the texture keeps its scale on every box.
    function createBox(name, part) {
        const scale = part.ts > 0 ? part.ts : 1;
        const sideUv = new BABYLON.Vector4(0, 0, part.w / scale, part.h / scale);
        const endUv = new BABYLON.Vector4(0, 0, part.d / scale, part.h / scale);
        const topUv = new BABYLON.Vector4(0, 0, part.w / scale, part.d / scale);
        const box = BABYLON.MeshBuilder.CreateBox(name, {
            width: part.w,
            height: part.h,
            depth: part.d,
            faceUV: [sideUv, sideUv, endUv, endUv, topUv, topUv],
            wrap: true,
        }, scene);
        box.material = getBoxMaterial(part.t, part.c);
        return box;
    }

    function loadModel(url) {
        let loading = models.get(url);
        if (!loading) {
            // Logged once per URL, because the failed load stays cached.
            loading = BABYLON.LoadAssetContainerAsync(url, scene).catch(error => {
                console.warn(`The model ${url} could not be loaded: ${error?.message ?? error}`);
                return null;
            });
            models.set(url, loading);
        }

        return loading;
    }

    function loadPerson(name) {
        let loading = people.get(name);
        if (!loading) {
            // Logged once per person, because the failed load stays cached.
            loading = BABYLON.LoadAssetContainerAsync(`${options.baseUrl}models/people/${name}.glb`, scene).catch(error => {
                console.warn(`The person ${name} could not be loaded: ${error?.message ?? error}`);
                return null;
            });
            people.set(name, loading);
        }

        return loading;
    }

    function getWindMaterial(material) {
        let windMaterial = windMaterials.get(material);
        if (!windMaterial) {
            windMaterial = material.clone(`${material.name}-wind`);
            addWind(windMaterial);
            // The shadow map draws with the material's own shader, so that the shadow sways with the wind.
            windMaterial.shadowDepthWrapper = new BABYLON.ShadowDepthWrapper(windMaterial, scene);
            windMaterial.gameWindSource = material;
            windMaterials.set(material, windMaterial);
        }

        return windMaterial;
    }

    function addWind(material) {
        const Plugin = getWindPlugin();
        material.gameWind = new Plugin(material);
    }

    // A glTF material has a fixed transparency mode, with which Babylon ignores the mesh visibility for blending, so a faded
    // mesh shows this blending copy instead. The depth pre-pass shows only the nearest surface, not the faces behind it.
    function getFadeMaterial(material) {
        let fadeMaterial = fadeMaterials.get(material);
        if (!fadeMaterial) {
            // Cloned from the source, because a clone of the wind copy would not carry its plugin reliably.
            const source = material.gameWindSource ?? material;
            fadeMaterial = source.clone(`${material.name}-fade`);
            // No shadow depth wrapper: Babylon flips the depth pre-pass define twice per frame, each flip creates the
            // material's effect anew, and the wrapper then compiles a new shadow program for every faded submesh. The
            // default shadow shader draws the faded shadow instead, which does not sway while the object is faded.
            if (material.gameWindSource) {
                addWind(fadeMaterial);
            }

            fadeMaterial.transparencyMode = BABYLON.Material.MATERIAL_ALPHABLEND;
            fadeMaterial.needDepthPrePass = true;
            fadeMaterials.set(material, fadeMaterial);
        }

        return fadeMaterial;
    }

    // The shared materials stay untouched, so fading one object never changes another. A see-through mesh has no outline.
    function showMesh(mesh, visibility) {
        mesh.visibility = visibility;
        const material = visibility < 1 && mesh.gameMaterial ? getFadeMaterial(mesh.gameMaterial) : mesh.gameMaterial;
        if (mesh.material !== material) {
            mesh.material = material;
        }

        options.renderer.setOutlined(mesh, visibility === 1);
    }

    function addMeshes(entity, meshes) {
        entity.changedAtFrame = frame;
        for (const mesh of meshes) {
            mesh.isPickable = false;
            mesh.gameMaterial = mesh.material;
            options.renderer.addMesh(mesh);
            showMesh(mesh, entity.visibility);
            entity.meshes.push(mesh);
        }
    }

    // A part hangs on a pivot node that turns it, with an offset node at the south-west bottom corner of its box below,
    // so that gliding moves only these two nodes.
    function createPart(entity, part, index) {
        const name = `${entity.view.id}-${index}`;
        const pivot = new BABYLON.TransformNode(`${name}-pivot`, scene);
        pivot.parent = entity.root;
        pivot.position.copyFromFloats(part.px, 0, part.pz);
        pivot.rotation.y = part.r * RADIANS_PER_DEGREE;
        const offset = new BABYLON.TransformNode(`${name}-offset`, scene);
        offset.parent = pivot;
        offset.position.copyFromFloats(part.x - part.px, part.y, part.z - part.pz);
        const node = { part, name, pivot, offset, instance: null, meshes: null, state: null, glide: null, animationGroup: null };
        stats.parts++;
        if (part.k === 'model') {
            addModel(entity, node, name).catch(error => console.warn(`The model ${part.m} could not be shown: ${error?.message ?? error}`));
        } else {
            const box = createBox(name, part);
            box.parent = offset;
            box.position.copyFromFloats(part.w / 2, part.h / 2, part.d / 2);
            addMeshes(entity, [box]);
        }

        return node;
    }

    async function addModel(entity, node, name) {
        const container = await loadModel(node.part.m);
        if (entity.isDisposed || isViewDisposed) {
            return;
        }

        if (container) {
            showModel(entity, node, name, container);
        } else {
            showFallback(entity, node, name);
        }

        updateStats();
    }

    function showModel(entity, node, name, container) {
        const part = node.part;
        const prefix = `${name}-`;
        const instance = container.instantiateModelsToScene(nodeName => prefix + nodeName, false, { doNotInstantiate: true });
        // The root keeps the loader's handedness conversion; its bounds are measured before it gets a parent or a scale.
        const root = instance.rootNodes[0];
        const { min, max } = root.getHierarchyBoundingVectors(true);
        const scale = Math.min(
            part.w / Math.max(max.x - min.x, 1e-6),
            part.h / Math.max(max.y - min.y, 1e-6),
            part.d / Math.max(max.z - min.z, 1e-6));
        root.scaling.scaleInPlace(scale);
        root.parent = node.offset;
        root.position.copyFromFloats(part.w / 2, -min.y * scale, part.d / 2);
        const meshes = root.getChildMeshes(false);
        if (part.e === 'wind') {
            for (const mesh of meshes) {
                if (mesh.material) {
                    mesh.material = getWindMaterial(mesh.material);
                }
            }
        }

        for (const group of instance.animationGroups) {
            group.stop();
        }

        addMeshes(entity, meshes);
        node.instance = instance;
        node.meshes = meshes;
        node.state = 'model';
        stats.models++;
        playAnimation(node);
    }

    // Plays the part's animation from the start: looping, or once, after which it holds its last frame, because a group that
    // does not loop leaves its targets at the end. The previous group is reset before it stops, for the reason at stopGroup.
    function playAnimation(node) {
        node.animationGroup?.reset();
        node.animationGroup?.stop();
        const animation = node.part.a;
        const name = `${node.name}-${animation}`;
        const group = animation ? node.instance.animationGroups.find(candidate => candidate.name === name) ?? null : null;
        node.animationGroup = group;
        if (!group) {
            if (animation) {
                warnMissingAnimation(node.part.m, animation);
            }

            return;
        }

        const isOnce = isOnceAnimation(node.part);
        group.start(!isOnce);
        if (isOnce) {
            stats.animationsOnce++;
        }
    }

    function isOnceAnimation(part) {
        return part.am === ONCE_ANIMATION_MODE;
    }

    function warnMissingAnimation(url, animation) {
        const key = `${url}|${animation}`;
        if (!missingAnimations.has(key)) {
            missingAnimations.add(key);
            console.warn(`The model ${url} has no animation ${animation}.`);
        }
    }

    function showFallback(entity, node, name) {
        const part = node.part;
        const box = createBox(`${name}-fallback`, { ...part, t: undefined, c: FALLBACK_COLOR });
        box.parent = node.offset;
        box.position.copyFromFloats(part.w / 2, part.h / 2, part.d / 2);
        addMeshes(entity, [box]);
        node.state = 'fallback';
        stats.failures++;
    }

    function createObject(entity) {
        entity.root = new BABYLON.TransformNode(entity.view.id, scene);
        entity.partNodes = entity.view.parts.map((part, index) => createPart(entity, part, index));
    }

    function createLabel(view, parent) {
        const texture = new BABYLON.DynamicTexture(`${view.id}-label`, { width: 512, height: 96 }, scene, true);
        texture.hasAlpha = true;
        const context = texture.getContext();
        context.clearRect(0, 0, 512, 96);
        context.fillStyle = 'rgba(0, 0, 0, 0.55)';
        context.beginPath();
        context.roundRect(4, 14, 504, 68, 16);
        context.fill();
        context.fillStyle = '#ffffff';
        context.font = '500 40px Roboto, Arial, sans-serif';
        context.textAlign = 'center';
        context.textBaseline = 'middle';
        context.fillText(view.isSelf ? `${view.title} (you)` : view.title, 256, 50, 480);
        texture.update();
        const material = new BABYLON.StandardMaterial(`${view.id}-label`, scene);
        material.diffuseTexture = texture;
        material.emissiveColor = BABYLON.Color3.White();
        material.disableLighting = true;
        material.useAlphaFromDiffuseTexture = true;
        const plane = BABYLON.MeshBuilder.CreatePlane(`${view.id}-label`, { width: 6, height: 1.125 }, scene);
        plane.material = material;
        plane.billboardMode = BABYLON.Mesh.BILLBOARDMODE_ALL;
        plane.isPickable = false;
        // A later rendering group starts with a cleared depth buffer, so walls, roofs and trees never hide a name.
        plane.renderingGroupId = 1;
        plane.parent = parent;
        plane.position.y = view.height + 1.4;
        return material;
    }

    // The label shows at once; the person follows when its model is loaded.
    function createPlayer(entity) {
        const view = entity.view;
        const holder = new BABYLON.TransformNode(view.id, scene);
        // Every player owns its label material and the materials cloned for its person.
        entity.materials.push(createLabel(view, holder));
        entity.root = holder;
        entity.animations = { idle: null, walking: null, isWalking: false, emote: null, emoteSequence: null };
        addPerson(entity).catch(error => console.warn(`The person ${view.character} could not be shown: ${error?.message ?? error}`));
    }

    async function addPerson(entity) {
        const container = await loadPerson(entity.view.character);
        if (entity.isDisposed || isViewDisposed) {
            return;
        }

        if (container) {
            try {
                showPerson(entity, container);
            } catch (error) {
                console.warn(`The person ${entity.view.character} could not be shown: ${error?.message ?? error}`);
                discardPerson(entity);
                showPersonFallback(entity);
            }
        } else {
            showPersonFallback(entity);
        }

        updateStats();
    }

    function showPerson(entity, container) {
        const view = entity.view;
        const prefix = `${view.id}-`;
        const instance = container.instantiateModelsToScene(name => prefix + name, true);
        entity.instance = instance;
        // The root keeps the loader's handedness conversion, which leaves the person looking north (+z); its bounds are
        // measured before it gets a parent or a scale.
        const model = instance.rootNodes[0];
        const { min, max } = model.getHierarchyBoundingVectors(true);
        const scale = view.height / (max.y - min.y);
        model.scaling.scaleInPlace(scale);
        model.position.y = -min.y * scale;
        model.parent = entity.root;

        const meshes = model.getChildMeshes(false);
        const materials = [...new Set(meshes.map(mesh => mesh.material).filter(Boolean))];
        const shirtName = prefix + (SHIRT_MATERIALS[view.character] ?? SHIRT_MATERIAL);
        const shirt = materials.find(material => material.name === shirtName);
        if (shirt) {
            const color = BABYLON.Color3.FromHexString(view.color);
            if ('albedoColor' in shirt) {
                shirt.albedoColor = color.toLinearSpace();
            } else {
                shirt.diffuseColor = color;
            }
        }

        for (const mesh of meshes) {
            mesh.isPickable = false;
            options.renderer.addMesh(mesh);
        }

        const find = name => instance.animationGroups.find(group => group.name === prefix + name);
        for (const group of instance.animationGroups) {
            group.stop();
        }

        const animations = entity.animations;
        animations.idle = find(IDLE_ANIMATION);
        animations.walking = find(WALKING_ANIMATION);
        if (animations.isWalking) {
            animations.walking?.start(true, WALKING_SPEED_RATIO);
        } else {
            animations.idle?.start(true);
        }

        entity.meshes = meshes;
        applyAnimation(entity, entity.view);
        entity.materials.push(...materials);
    }

    // Releases what a failed showPerson created; its cloned materials are not in the entity's list yet.
    function discardPerson(entity) {
        const instance = entity.instance;
        if (instance) {
            const meshes = instance.rootNodes.flatMap(node => node.getChildMeshes(false));
            disposeInstance(instance, meshes);
            for (const node of instance.rootNodes) {
                node.dispose(false, true);
            }
        }

        entity.instance = null;
        entity.meshes = [];
        const animations = entity.animations;
        animations.idle = null;
        animations.walking = null;
        animations.emote = null;
    }

    function showPersonFallback(entity) {
        const view = entity.view;
        const box = createBox(`${view.id}-fallback`, { w: view.width, h: view.height, d: view.depth, c: FALLBACK_COLOR });
        box.parent = entity.root;
        box.position.y = view.height / 2;
        box.isPickable = false;
        options.renderer.addMesh(box);
        entity.meshes = [box];
        entity.isFallback = true;
        stats.failures++;
    }

    // Plays the animation the server reports: once animations play to their end and return to idle, loop animations hold
    // until the server clears them, and a step ends either. Each run has its own sequence, so a run is started at most once
    // and the same animation twice in a row plays twice.
    function applyAnimation(entity, view) {
        const animations = entity.animations;
        if (!animations || !entity.instance) {
            return;
        }

        if (!view.animation) {
            animations.emoteSequence = view.animationSequence;
            stopEmote(animations);
            return;
        }

        if (animations.emoteSequence === view.animationSequence) {
            return;
        }

        animations.emoteSequence = view.animationSequence;
        const group = entity.instance.animationGroups.find(candidate => candidate.name === `${view.id}-${view.animation}`);
        if (!group) {
            warnMissingAnimation(`${options.baseUrl}models/people/${view.character}.glb`, view.animation);
            return;
        }

        clearEmote(animations);
        stopGroup(animations.idle);
        stopGroup(animations.walking);
        // The emote takes over a walk that is still showing, so that the next step stops it and walks again.
        animations.isWalking = false;
        const isOnce = view.animationMode === ONCE_ANIMATION_MODE;
        group.start(!isOnce);
        animations.emote = group;
        stats.emotes++;
        updateStats();
        if (isOnce) {
            group.onAnimationGroupEndObservable.addOnce(() => {
                if (animations.emote === group && !entity.isDisposed) {
                    animations.emote = null;
                    resumeMovement(animations);
                }
            });
        }
    }

    function stopEmote(animations) {
        if (animations.emote) {
            clearEmote(animations);
            resumeMovement(animations);
        }
    }

    // Cleared before the group stops, because stopping a group that plays once runs its end handler, which resumes the
    // movement only while it is still the emote.
    function clearEmote(animations) {
        const emote = animations.emote;
        animations.emote = null;
        stopGroup(emote);
    }

    function resumeMovement(animations) {
        if (animations.isWalking) {
            animations.walking?.start(true, WALKING_SPEED_RATIO);
        } else {
            animations.idle?.start(true);
        }
    }

    // Reset before it stops, because stopping leaves the bones where they are, such as the lowered pelvis of a sitting
    // person, and the next group may not move all of them. Only a playing group is reset: resetting a stopped one plays
    // its first frame.
    function stopGroup(group) {
        if (group?.isStarted) {
            group.reset();
            group.stop();
        }
    }

    function createEntity(view) {
        const entity = {
            view, root: null, tween: null, idleAt: 0, isFaded: false, lastHitAt: -Infinity, visibility: 1, changedAtFrame: 0,
            isDisposed: false, isFallback: false,
            meshes: [], materials: [], partNodes: [], animations: null, instance: null,
        };
        if (view.kind === 'player') {
            createPlayer(entity);
        } else {
            createObject(entity);
        }

        updateStats();
        return entity;
    }

    function disposeInstance(instance, meshes) {
        for (const group of instance.animationGroups) {
            group.dispose();
        }

        for (const skeleton of instance.skeletons) {
            skeleton.dispose();
        }

        for (const mesh of meshes) {
            mesh.morphTargetManager?.dispose();
            releaseWindShadow(mesh);
        }
    }

    // A ShadowDepthWrapper keeps every mesh it drew in a map that the mesh's dispose does not clean, so a disposed tree
    // would stay referenced by the wrapper of its wind material.
    function releaseWindShadow(mesh) {
        const material = mesh.gameMaterial;
        if (material?.gameWind) {
            material.shadowDepthWrapper._meshes?.delete(mesh);
        }
    }

    function disposePart(node) {
        stats.parts--;
        if (node.state === 'model') {
            stats.models--;
            disposeInstance(node.instance, node.meshes);
        } else if (node.state === 'fallback') {
            stats.failures--;
        }
    }

    // Shared box, model and wind materials stay; the scene disposes them.
    function disposeEntity(entity) {
        entity.isDisposed = true;
        if (entity.isFaded) {
            stats.faded--;
        }

        if (entity.instance) {
            disposeInstance(entity.instance, entity.meshes);
        }

        if (entity.isFallback) {
            stats.failures--;
        }

        for (const material of entity.materials) {
            material.dispose(true, true);
        }

        for (const node of entity.partNodes) {
            disposePart(node);
        }

        entity.root.dispose(false, false);
        updateStats();
    }

    function place(entity, x, y) {
        entity.root.position = positionOf(entity.view, x, y);
        entity.changedAtFrame = frame;
        entity.tween = null;
        setWalking(entity, false);
    }

    function moveTo(entity, x, y) {
        const target = positionOf(entity.view, x, y);
        if (BABYLON.Vector3.Distance(entity.root.position, target) > TELEPORT_DISTANCE) {
            place(entity, x, y);
            return;
        }

        // An object glides like a part, a little longer than the interval it is published at, so that a walking animal
        // moves on without stopping between its steps.
        const duration = entity.view.kind === 'player' ? options.stepIntervalMs : GLIDE_MS;
        entity.tween = { from: entity.root.position.clone(), to: target, start: performance.now(), duration };
        setWalking(entity, true);
    }

    function setWalking(entity, isWalking) {
        entity.idleAt = 0;
        const animations = entity.animations;
        if (!animations || animations.isWalking === isWalking) {
            return;
        }

        animations.isWalking = isWalking;
        if (isWalking) {
            // A step ends a running emote; the server clears it on the same step.
            clearEmote(animations);
            stopGroup(animations.idle);
            animations.walking?.start(true, WALKING_SPEED_RATIO);
        } else if (!animations.emote) {
            stopGroup(animations.walking);
            animations.idle?.start(true);
        }
    }

    function setFacing(entity, facing) {
        if (entity.view.kind === 'player') {
            entity.root.rotation.y = facing * Math.PI / 4;
        }
    }

    function hideTarget() {
        for (const marker of targetMarkers) {
            marker.setEnabled(false);
        }
    }

    function setFaded(entity, isFaded) {
        if (entity.isFaded === isFaded) {
            return;
        }

        entity.isFaded = isFaded;
        stats.faded += isFaded ? 1 : -1;
        updateStats();
    }

    function glideVisibility(entity, elapsed) {
        const target = entity.isFaded ? FADED_VISIBILITY : 1;
        if (entity.visibility !== target) {
            const step = (1 - FADED_VISIBILITY) * elapsed / FADE_MS;
            setVisibility(entity, target < entity.visibility ? Math.max(target, entity.visibility - step) : Math.min(target, entity.visibility + step));
        }
    }

    function setVisibility(entity, visibility) {
        entity.visibility = visibility;
        for (const mesh of entity.meshes) {
            showMesh(mesh, visibility);
        }
    }

    // What neither a glide nor a new animation can update: a change of any of it rebuilds the object.
    function getPartShape(part) {
        return [part.k, part.w, part.h, part.d, part.px, part.pz, part.t, part.ts, part.c, part.m, part.e].join('|');
    }

    function haveSameShapes(previous, parts) {
        return previous.length === parts.length && previous.every((part, index) => getPartShape(part) === getPartShape(parts[index]));
    }

    // The own player's label reads "(you)", so becoming or ceasing to be the own player recreates it.
    function hasShapeChanged(previous, view) {
        if (previous.kind !== view.kind) {
            return true;
        }

        if (view.kind !== 'player') {
            return !haveSameShapes(previous.parts, view.parts);
        }

        return previous.width !== view.width || previous.depth !== view.depth || previous.height !== view.height
            || previous.color !== view.color || previous.character !== view.character || previous.title !== view.title
            || previous.isSelf !== view.isSelf;
    }

    function updateParts(entity, parts) {
        entity.partNodes.forEach((node, index) => {
            const part = parts[index];
            const previous = node.part;
            if (part.x !== previous.x || part.y !== previous.y || part.z !== previous.z || part.r !== previous.r) {
                node.glide = {
                    fromOffset: node.offset.position.clone(),
                    toOffset: new BABYLON.Vector3(part.x - part.px, part.y, part.z - part.pz),
                    fromRotation: node.pivot.rotation.y,
                    rotationDelta: getShortestTurn(node.pivot.rotation.y, part.r * RADIANS_PER_DEGREE),
                    toRotation: part.r * RADIANS_PER_DEGREE,
                    start: performance.now(),
                };
            }

            node.part = part;
            // A model still loading plays the latest animation once it is shown.
            if (node.state === 'model'
                && (part.a !== previous.a || isOnceAnimation(part) !== isOnceAnimation(previous))) {
                playAnimation(node);
                updateStats();
            }
        });
    }

    // The turn from one angle to another in radians, in -PI to PI, so that 170 to -170 degrees turns by 20 degrees.
    function getShortestTurn(from, to) {
        const turn = (to - from) % TWO_PI;
        if (turn > Math.PI) {
            return turn - TWO_PI;
        }

        return turn < -Math.PI ? turn + TWO_PI : turn;
    }

    function animateGlide(node, now) {
        const glide = node.glide;
        const progress = Math.min(1, (now - glide.start) / GLIDE_MS);
        // Eases out, so a door leaf slows down before it stops.
        const eased = 1 - (1 - progress) * (1 - progress);
        BABYLON.Vector3.LerpToRef(glide.fromOffset, glide.toOffset, eased, node.offset.position);
        node.pivot.rotation.y = glide.fromRotation + glide.rotationDelta * eased;
        if (progress >= 1) {
            node.pivot.rotation.y = glide.toRotation;
            node.glide = null;
        }
    }

    function clearSelf() {
        selfId = null;
        hideTarget();
        for (const other of entities.values()) {
            other.lastHitAt = -Infinity;
            setFaded(other, false);
            if (other.visibility !== 1) {
                setVisibility(other, 1);
            }
        }
    }

    // Points the reused sightline from the eye to the own player's head and records a hit on every object whose triangles
    // it meets before the head.
    function checkSightline(eye, self, now) {
        const offsetX = self.root.position.x - eye.x;
        const offsetY = self.view.height * HEAD_HEIGHT - eye.y;
        const offsetZ = self.root.position.z - eye.z;
        const length = Math.hypot(offsetX, offsetY, offsetZ);
        if (length < 1e-6) {
            return;
        }

        sightline.origin.copyFrom(eye);
        sightline.direction.copyFromFloats(offsetX / length, offsetY / length, offsetZ / length);
        sightline.length = length;
        for (const entity of entities.values()) {
            // Until the scene rendered once after an object was added or placed, its meshes keep the world matrix and bounds
            // of the origin, next to the own player, and would fade it for a moment.
            if (entity.view.kind !== 'player' && entity.changedAtFrame < frame - 1 && hides(entity, eye, offsetX, offsetY, offsetZ)) {
                entity.lastHitAt = now;
            }
        }
    }

    // The bounds of a mesh, such as a trunk with leaning branches, can cross the sightline where no triangle does, so they
    // only select the meshes to test exactly.
    function hides(entity, eye, offsetX, offsetY, offsetZ) {
        for (const mesh of entity.meshes) {
            // A mesh without vertices, such as the node above the primitives of a model, has an empty box at its origin.
            if (mesh.getTotalVertices() > 0
                && crossesBox(mesh.getBoundingInfo().boundingBox, eye, offsetX, offsetY, offsetZ)
                && sightline.intersectsMesh(mesh, false).hit) {
                return true;
            }
        }

        return false;
    }

    // Whether the segment from the eye by the offset crosses the box, clipping the part of the segment inside it one axis
    // at a time; allocation free.
    function crossesBox(box, eye, offsetX, offsetY, offsetZ) {
        const min = box.minimumWorld;
        const max = box.maximumWorld;
        segment.enter = 0;
        segment.exit = 1;
        return clip(eye.x, offsetX, min.x, max.x) && clip(eye.y, offsetY, min.y, max.y) && clip(eye.z, offsetZ, min.z, max.z);
    }

    function clip(start, offset, low, high) {
        if (Math.abs(offset) < 1e-6) {
            return start >= low && start <= high;
        }

        let near = (low - start) / offset;
        let far = (high - start) / offset;
        if (near > far) {
            const swap = near;
            near = far;
            far = swap;
        }

        segment.enter = Math.max(segment.enter, near);
        segment.exit = Math.min(segment.exit, far);
        return segment.enter <= segment.exit;
    }

    return {
        /** Adds or updates the object; returns true when it was the own player and is not anymore. */
        upsert(view) {
            const isSelfLost = !view.isSelf && view.id === selfId;
            if (isSelfLost) {
                clearSelf();
            }

            if (view.isSelf) {
                selfId = view.id;
                ensureOrigin(view.x, view.y);
            } else if (originX === null) {
                ensureOrigin(view.x, view.y);
            }

            let entity = entities.get(view.id);
            if (!entity || hasShapeChanged(entity.view, view)) {
                const previous = entity?.view;
                if (entity) {
                    disposeEntity(entity);
                }

                // A recreated own player keeps the predicted position, which the input owns.
                const shown = view.isSelf && previous?.isSelf ? { ...view, x: previous.x, y: previous.y, facing: previous.facing } : view;
                entity = createEntity(shown);
                entities.set(view.id, entity);
                // The person loads asynchronously and plays the latest view's animation once it is shown.
                place(entity, shown.x, shown.y);
                setFacing(entity, shown.facing);
                return isSelfLost;
            }

            const hasMoved = entity.view.x !== view.x || entity.view.y !== view.y;
            const previousFacing = entity.view.facing;
            if (view.kind !== 'player') {
                updateParts(entity, view.parts);
            }

            // The own player's position and facing belong to the input, which predicts them.
            entity.view = view.isSelf ? { ...view, x: entity.view.x, y: entity.view.y, facing: previousFacing } : view;
            if (!view.isSelf) {
                if (hasMoved) {
                    moveTo(entity, view.x, view.y);
                }

                setFacing(entity, view.facing);
            }

            applyAnimation(entity, view);
            return isSelfLost;
        },

        /** Removes the object; returns true when it was the own player. */
        remove(id) {
            const entity = entities.get(id);
            if (entity) {
                disposeEntity(entity);
                entities.delete(id);
            }

            if (id !== selfId) {
                return false;
            }

            clearSelf();
            return true;
        },

        /** Releases what the scene does not own; call before disposing the scene. */
        dispose() {
            isViewDisposed = true;
            for (const loading of [...models.values(), ...people.values()]) {
                loading.then(container => container?.dispose());
            }
        },

        setSelf(x, y, facing, animate) {
            const entity = entities.get(selfId);
            if (!entity) {
                return;
            }

            const hasMoved = entity.view.x !== x || entity.view.y !== y;
            entity.view = { ...entity.view, x, y, facing };
            ensureOrigin(x, y);
            if (hasMoved) {
                if (animate) {
                    moveTo(entity, x, y);
                } else {
                    place(entity, x, y);
                }
            }

            setFacing(entity, facing);
        },

        getSelf() {
            return entities.get(selfId)?.view ?? null;
        },

        getBlockers() {
            const blockers = [];
            for (const entity of entities.values()) {
                if (entity.view.id !== selfId && entity.view.isMovementBlocking) {
                    blockers.push(entity.view);
                }
            }

            return blockers;
        },

        setTarget(tiles) {
            while (targetMarkers.length < tiles.length) {
                const marker = BABYLON.MeshBuilder.CreateGround(`target-${targetMarkers.length}`, { width: 0.92, height: 0.92 }, scene);
                marker.material = targetMaterial;
                marker.isPickable = false;
                targetMarkers.push(marker);
            }

            targetMarkers.forEach((marker, index) => {
                const tile = tiles[index];
                const isVisible = Boolean(tile) && originX !== null && selfId !== null;
                marker.setEnabled(isVisible);
                if (isVisible) {
                    marker.position.copyFromFloats(tile.x - originX + 0.5, 0.04, tile.y - originY + 0.5);
                }
            });
        },

        animate(now) {
            frame++;
            const seconds = now / 1000;
            windSwayPhase = (seconds * WIND_SPEED) % TWO_PI;
            windGustPhase = (seconds * GUST_SPEED) % TWO_PI;
            for (const entity of entities.values()) {
                for (const node of entity.partNodes) {
                    if (node.glide) {
                        animateGlide(node, now);
                    }
                }

                const tween = entity.tween;
                if (tween) {
                    const progress = Math.min(1, (now - tween.start) / tween.duration);
                    BABYLON.Vector3.LerpToRef(tween.from, tween.to, progress, entity.root.position);
                    if (progress >= 1) {
                        entity.tween = null;
                        entity.idleAt = now + idleDelay;
                    }
                } else if (entity.idleAt !== 0 && now >= entity.idleAt) {
                    setWalking(entity, false);
                }
            }

            const self = entities.get(selfId);
            if (!self) {
                return null;
            }

            // The ground follows in whole meters, so its grid stays on the tiles.
            ground.position.x = Math.round((self.root.position.x + originX) / METER) * METER - originX;
            ground.position.z = Math.round((self.root.position.z + originY) / METER) * METER - originY;
            selfPosition.x = self.root.position.x;
            selfPosition.z = self.root.position.z;
            return selfPosition;
        },

        // Fades the objects between the camera and the own player's head, including those that do not block movement, such
        // as an open door whose lintel hides the head.
        afterCameraMoved(camera, now) {
            const self = entities.get(selfId);
            if (!self) {
                return;
            }

            const elapsed = Math.min(FADE_MS, Math.max(0, now - lastFadeFrame));
            lastFadeFrame = now;
            if (now - lastSightlineCheck >= SIGHTLINE_CHECK_MS) {
                lastSightlineCheck = now;
                checkSightline(camera.position, self, now);
            }

            for (const entity of entities.values()) {
                if (entity.view.kind !== 'player') {
                    setFaded(entity, now - entity.lastHitAt < FADE_HOLD_MS);
                    glideVisibility(entity, elapsed);
                }
            }
        },
    };
}
