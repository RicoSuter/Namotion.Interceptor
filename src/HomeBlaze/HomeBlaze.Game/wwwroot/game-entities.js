// Draws the objects of the view. Every object but a player is built from the parts of its visual description: boxes with
// shared materials and glTF models, each loaded once per URL. Players are animated people. World coordinates are relative
// to an integer origin near the own player, so the GPU's 32-bit floats stay precise anywhere in the int range. One unit
// is one tile of 25 cm.
const REBASE_DISTANCE = 2048;
const TELEPORT_DISTANCE = 6;
// One meter, the size of one grass repeat and the spacing of the ground grid.
const METER = 4;
const FADED_VISIBILITY = 0.3;
const HEAD_HEIGHT = 0.8;
// Keeps a player walking for this part of a step after its move ended, so the next step does not flicker to idle.
const IDLE_DELAY_STEPS = 0.75;
// Lets the feet of the walk cycle keep pace with one tile per step interval, about 1.4 m/s.
const WALKING_SPEED_RATIO = 1.05;
// The names in the model file; the shirt takes the player's color.
const IDLE_ANIMATION = 'Idle_Neutral';
const WALKING_ANIMATION = 'Walk';
const SHIRT_MATERIAL = 'LightBrown';
// A part glides this long to a new offset or rotation, such as a door leaf that swings open.
const GLIDE_MS = 300;
const RADIANS_PER_DEGREE = Math.PI / 180;
// The color of a box without texture or color, and of the box that replaces a model that failed to load.
const FALLBACK_COLOR = '#8d8577';
// Wind bends a part by this many tiles per square tile of height above the object's base, swaying at the first speed and
// gusting at the second, both in radians per second.
const WIND_STRENGTH = 0.0015;
const WIND_SPEED = 1.7;
const GUST_SPEED = 0.37;
// Runs after the vertex is in world space; finalWorld[3] is the mesh's world position, which spreads the phase so that
// neighboring trees do not sway in step.
const WIND_SHADER = `
#ifdef GAME_WIND
    float gameWindHeight = max(worldPos.y - finalWorld[3].y, 0.0);
    float gameWindPhase = finalWorld[3].x * 0.13 + finalWorld[3].z * 0.07;
    float gameWindSway = sin(gameWindTime * ${toGlsl(WIND_SPEED)} + gameWindPhase) * 0.7
        + sin(gameWindTime * ${toGlsl(GUST_SPEED)} + gameWindPhase * 0.5) * 0.3;
    float gameWindBend = gameWindHeight * gameWindHeight * ${toGlsl(WIND_STRENGTH)} * gameWindSway;
    worldPos.x += gameWindBend;
    worldPos.z += gameWindBend * 0.4;
#endif
`;

// Seconds of the render clock, which the wind shader reads; the views of a tab share it.
let windTime = 0;
let WindPlugin = null;

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
                ubo: [{ name: 'gameWindTime', size: 1, type: 'float' }],
                vertex: '#ifdef GAME_WIND\nuniform float gameWindTime;\n#endif\n',
            };
        }

        bindForSubMesh(uniformBuffer) {
            uniformBuffer.updateFloat('gameWindTime', windTime);
        }

        getCustomCode(shaderType) {
            return shaderType === 'vertex' ? { CUSTOM_VERTEX_UPDATE_WORLDPOS: WIND_SHADER } : null;
        }
    };
    return WindPlugin;
}

export async function createEntities(scene, options) {
    const BABYLON = window.BABYLON;
    const entities = new Map();
    const targetMarkers = [];
    // Shared by the objects of the view: box materials by texture and color, model loads by URL, and the wind variant of
    // each model material.
    const boxMaterials = new Map();
    const models = new Map();
    const windMaterials = new Map();
    // Written to the canvas for tests and diagnosis.
    const stats = { parts: 0, models: 0, failures: 0 };
    // Reused by every frame, so the render loop does not allocate.
    const selfPosition = { x: 0, z: 0 };
    const segment = { enter: 0, exit: 1 };
    const idleDelay = options.stepIntervalMs * IDLE_DELAY_STEPS;
    let originX = null;
    let originY = null;
    let selfId = null;

    const ground = BABYLON.MeshBuilder.CreateGround('ground', { width: 600, height: 600 }, scene);
    ground.material = await createGroundMaterial();
    ground.isPickable = false;

    const targetMaterial = new BABYLON.StandardMaterial('target', scene);
    targetMaterial.emissiveColor = new BABYLON.Color3(1, 0.88, 0.4);
    targetMaterial.disableLighting = true;
    targetMaterial.alpha = 0.4;

    const blobMaterial = new BABYLON.StandardMaterial('blob', scene);
    blobMaterial.diffuseColor = BABYLON.Color3.Black();
    blobMaterial.specularColor = BABYLON.Color3.Black();
    blobMaterial.alpha = 0.28;

    // Not added to the scene; every player instantiates its own copy.
    const character = await BABYLON.LoadAssetContainerAsync(`${options.baseUrl}models/Casual_2.glb`, scene);
    updateStats();

    async function createGroundMaterial() {
        // The border of each repeat draws the meter grid; half of the stroke lies outside, so each edge shows half of it.
        // It stays wide enough to survive the smaller mipmaps that the usual camera distance samples.
        const image = new Image();
        image.src = `${options.baseUrl}textures/grass.jpg`;
        await image.decode();
        const texture = new BABYLON.DynamicTexture('grass', 512, scene, true);
        const context = texture.getContext();
        context.drawImage(image, 0, 0, 512, 512);
        context.strokeStyle = 'rgba(0, 0, 0, 0.16)';
        context.lineWidth = 8;
        context.strokeRect(0, 0, 512, 512);
        texture.update();
        // Dynamic textures clamp by default.
        texture.wrapU = BABYLON.Texture.WRAP_ADDRESSMODE;
        texture.wrapV = BABYLON.Texture.WRAP_ADDRESSMODE;
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

    function getWindMaterial(material) {
        let windMaterial = windMaterials.get(material);
        if (!windMaterial) {
            windMaterial = material.clone(`${material.name}-wind`);
            const Plugin = getWindPlugin();
            windMaterial.gameWind = new Plugin(windMaterial);
            windMaterials.set(material, windMaterial);
        }

        return windMaterial;
    }

    function addMeshes(entity, meshes) {
        const visibility = entity.isFaded ? FADED_VISIBILITY : 1;
        for (const mesh of meshes) {
            mesh.isPickable = false;
            mesh.visibility = visibility;
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
        const node = { part, pivot, offset, instance: null, state: null, glide: null };
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
        if (entity.isDisposed) {
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

        playAnimation(instance, prefix, part.a);
        addMeshes(entity, meshes);
        node.instance = instance;
        node.state = 'model';
        stats.models++;
    }

    function playAnimation(instance, prefix, animation) {
        for (const group of instance.animationGroups) {
            if (animation && group.name === prefix + animation) {
                group.start(true);
            } else {
                group.stop();
            }
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

    // The box around all parts relative to the object's corner, which fading tests; rotations are left out.
    function getBounds(parts) {
        if (parts.length === 0) {
            return { minX: 0, minY: 0, minZ: 0, maxX: 0, maxY: 0, maxZ: 0 };
        }

        const bounds = { minX: Infinity, minY: Infinity, minZ: Infinity, maxX: -Infinity, maxY: -Infinity, maxZ: -Infinity };
        for (const part of parts) {
            bounds.minX = Math.min(bounds.minX, part.x);
            bounds.minY = Math.min(bounds.minY, part.y);
            bounds.minZ = Math.min(bounds.minZ, part.z);
            bounds.maxX = Math.max(bounds.maxX, part.x + part.w);
            bounds.maxY = Math.max(bounds.maxY, part.y + part.h);
            bounds.maxZ = Math.max(bounds.maxZ, part.z + part.d);
        }

        return bounds;
    }

    function createObject(entity) {
        entity.root = new BABYLON.TransformNode(entity.view.id, scene);
        entity.bounds = getBounds(entity.view.parts);
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
        plane.parent = parent;
        plane.position.y = view.height + 1.4;
        return material;
    }

    function createPlayer(entity) {
        const view = entity.view;
        const holder = new BABYLON.TransformNode(view.id, scene);
        const prefix = `${view.id}-`;
        const instance = character.instantiateModelsToScene(name => prefix + name, true);
        // The root keeps the loader's handedness conversion, which leaves the character looking north (+z).
        const model = instance.rootNodes[0];
        model.parent = holder;
        const { min, max } = model.getHierarchyBoundingVectors(true);
        const scale = view.height / (max.y - min.y);
        model.scaling.scaleInPlace(scale);
        model.position.y = -min.y * scale;

        const meshes = model.getChildMeshes(false);
        // Every player owns the materials cloned for it; the blob material is shared.
        const materials = [...new Set(meshes.map(mesh => mesh.material).filter(Boolean))];
        const shirt = materials.find(material => material.name === prefix + SHIRT_MATERIAL);
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
        }

        const blob = BABYLON.MeshBuilder.CreateDisc(`${view.id}-blob`, { radius: view.width * 0.55, tessellation: 24 }, scene);
        blob.material = blobMaterial;
        blob.rotation.x = Math.PI / 2;
        blob.position.y = 0.02;
        blob.parent = holder;
        blob.isPickable = false;

        materials.push(createLabel(view, holder));

        const find = name => instance.animationGroups.find(group => group.name === prefix + name);
        const idle = find(IDLE_ANIMATION);
        const walking = find(WALKING_ANIMATION);
        for (const group of instance.animationGroups) {
            group.stop();
        }

        idle?.start(true);
        entity.root = holder;
        entity.animations = { idle, walking, isWalking: false };
        entity.instance = instance;
        entity.meshes = meshes;
        entity.materials = materials;
    }

    function createEntity(view) {
        const entity = {
            view, root: null, tween: null, idleAt: 0, isFaded: false, isDisposed: false,
            meshes: [], materials: [], partNodes: [], bounds: null, animations: null, instance: null,
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
        }
    }

    function disposePart(node) {
        stats.parts--;
        if (node.state === 'model') {
            stats.models--;
            disposeInstance(node.instance, []);
        } else if (node.state === 'fallback') {
            stats.failures--;
        }
    }

    // Shared box, model and wind materials stay; the scene disposes them.
    function disposeEntity(entity) {
        entity.isDisposed = true;
        if (entity.instance) {
            disposeInstance(entity.instance, entity.meshes);
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
        entity.tween = null;
        setWalking(entity, false);
    }

    function moveTo(entity, x, y) {
        const target = positionOf(entity.view, x, y);
        if (BABYLON.Vector3.Distance(entity.root.position, target) > TELEPORT_DISTANCE) {
            place(entity, x, y);
            return;
        }

        entity.tween = { from: entity.root.position.clone(), to: target, start: performance.now(), duration: options.stepIntervalMs };
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
            animations.idle?.stop();
            animations.walking?.start(true, WALKING_SPEED_RATIO);
        } else {
            animations.walking?.stop();
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
        const visibility = isFaded ? FADED_VISIBILITY : 1;
        for (const mesh of entity.meshes) {
            mesh.visibility = visibility;
        }
    }

    // What a glide cannot animate: a change of any of it rebuilds the object.
    function getPartShape(part) {
        return [part.k, part.w, part.h, part.d, part.px, part.pz, part.t, part.ts, part.c, part.m, part.e, part.a].join('|');
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
            || previous.color !== view.color || previous.title !== view.title || previous.isSelf !== view.isSelf;
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
                    toRotation: part.r * RADIANS_PER_DEGREE,
                    start: performance.now(),
                };
            }

            node.part = part;
        });
        entity.bounds = getBounds(parts);
    }

    function animateGlide(node, now) {
        const glide = node.glide;
        const progress = Math.min(1, (now - glide.start) / GLIDE_MS);
        // Eases out, so a door leaf slows down before it stops.
        const eased = 1 - (1 - progress) * (1 - progress);
        BABYLON.Vector3.LerpToRef(glide.fromOffset, glide.toOffset, eased, node.offset.position);
        node.pivot.rotation.y = glide.fromRotation + (glide.toRotation - glide.fromRotation) * eased;
        if (progress >= 1) {
            node.glide = null;
        }
    }

    function clearSelf() {
        selfId = null;
        hideTarget();
        for (const other of entities.values()) {
            setFaded(other, false);
        }
    }

    // Whether the segment from the start by the offset crosses the bounds of the entity, clipping the part of the segment
    // inside them one axis at a time; allocation free for the render loop.
    function crosses(entity, startX, startY, startZ, offsetX, offsetY, offsetZ) {
        const position = entity.root.position;
        const bounds = entity.bounds;
        segment.enter = 0;
        segment.exit = 1;
        return clip(startX, offsetX, position.x + bounds.minX, position.x + bounds.maxX)
            && clip(startY, offsetY, bounds.minY, bounds.maxY)
            && clip(startZ, offsetZ, position.z + bounds.minZ, position.z + bounds.maxZ);
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
            character.dispose();
            for (const loading of models.values()) {
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
            windTime = now / 1000;
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

        // Fades the movement blocking objects between the camera and the own player's head.
        afterCameraMoved(camera) {
            const self = entities.get(selfId);
            if (!self) {
                return;
            }

            const eye = camera.position;
            const offsetX = self.root.position.x - eye.x;
            const offsetY = self.view.height * HEAD_HEIGHT - eye.y;
            const offsetZ = self.root.position.z - eye.z;
            for (const entity of entities.values()) {
                if (entity.view.kind !== 'player') {
                    setFaded(entity, entity.view.isMovementBlocking && crosses(entity, eye.x, eye.y, eye.z, offsetX, offsetY, offsetZ));
                }
            }
        },
    };
}
