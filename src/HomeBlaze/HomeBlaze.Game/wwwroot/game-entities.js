// Draws the objects of the view: walls as brick boxes, players as animated people. World coordinates are relative to an
// integer origin near the own player, so the GPU's 32-bit floats stay precise anywhere in the int range. One unit is
// one tile of 25 cm.
const REBASE_DISTANCE = 2048;
const TELEPORT_DISTANCE = 6;
// One meter, the size of one grass repeat and the spacing of the ground grid.
const METER = 4;
// The brick texture shows about 2 m by 1 m of wall.
const BRICK_REPEAT_WIDTH = 8;
const BRICK_REPEAT_HEIGHT = 4;
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

export async function createEntities(scene, options) {
    const BABYLON = window.BABYLON;
    const entities = new Map();
    const targetMarkers = [];
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

    const wallMaterial = new BABYLON.StandardMaterial('wall', scene);
    wallMaterial.diffuseTexture = new BABYLON.Texture(`${options.baseUrl}textures/bricks.jpg`, scene);
    wallMaterial.specularColor = new BABYLON.Color3(0.04, 0.04, 0.04);

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

    function ensureOrigin(x, y) {
        if (originX === null || Math.abs(x - originX) > REBASE_DISTANCE || Math.abs(y - originY) > REBASE_DISTANCE) {
            originX = x;
            originY = y;
            for (const entity of entities.values()) {
                place(entity, entity.view.x, entity.view.y);
            }
        }
    }

    function centerOf(view, x, y) {
        // Walls stand on their center, players on their feet.
        const height = view.kind === 'player' ? 0 : view.height / 2;
        return new BABYLON.Vector3(x - originX + view.width / 2, height, y - originY + view.depth / 2);
    }

    function createWall(view) {
        const sideUv = new BABYLON.Vector4(0, 0, view.width / BRICK_REPEAT_WIDTH, view.height / BRICK_REPEAT_HEIGHT);
        const endUv = new BABYLON.Vector4(0, 0, view.depth / BRICK_REPEAT_WIDTH, view.height / BRICK_REPEAT_HEIGHT);
        const topUv = new BABYLON.Vector4(0, 0, view.width / BRICK_REPEAT_WIDTH, view.depth / BRICK_REPEAT_HEIGHT);
        const box = BABYLON.MeshBuilder.CreateBox(view.id, {
            width: view.width,
            height: view.height,
            depth: view.depth,
            faceUV: [sideUv, sideUv, endUv, endUv, topUv, topUv],
            wrap: true,
        }, scene);
        box.material = wallMaterial;
        box.metadata = { kind: 'wall' };
        return { root: box };
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

    function createPlayer(view) {
        const holder = new BABYLON.TransformNode(view.id, scene);
        holder.metadata = { kind: 'player' };
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
        return { root: holder, animations: { idle, walking, isWalking: false }, instance, meshes, materials };
    }

    function createEntity(view) {
        const created = view.kind === 'player' ? createPlayer(view) : createWall(view);
        return { view, tween: null, idleAt: 0, isFaded: false, ...created };
    }

    function disposeEntity(entity) {
        const instance = entity.instance;
        if (instance) {
            for (const group of instance.animationGroups) {
                group.dispose();
            }

            for (const skeleton of instance.skeletons) {
                skeleton.dispose();
            }

            for (const mesh of entity.meshes) {
                mesh.morphTargetManager?.dispose();
            }

            for (const material of entity.materials) {
                material.dispose(true, true);
            }
        }

        entity.root.dispose(false, false);
    }

    function place(entity, x, y) {
        entity.root.position = centerOf(entity.view, x, y);
        entity.tween = null;
        setWalking(entity, false);
    }

    function moveTo(entity, x, y) {
        const target = centerOf(entity.view, x, y);
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
        if (entity.isFaded !== isFaded) {
            entity.isFaded = isFaded;
            entity.root.visibility = isFaded ? FADED_VISIBILITY : 1;
        }
    }

    // The own player's label reads "(you)", so becoming or ceasing to be the own player recreates it.
    function hasShapeChanged(previous, view) {
        return previous.kind !== view.kind || previous.width !== view.width || previous.depth !== view.depth
            || previous.height !== view.height || previous.color !== view.color || previous.title !== view.title
            || previous.isSelf !== view.isSelf;
    }

    function clearSelf() {
        selfId = null;
        hideTarget();
        for (const other of entities.values()) {
            setFaded(other, false);
        }
    }

    // Whether the segment from the start by the offset crosses the box of the entity, clipping the part of the segment
    // inside the box one axis at a time; allocation free for the render loop.
    function crosses(entity, startX, startY, startZ, offsetX, offsetY, offsetZ) {
        const center = entity.root.position;
        const view = entity.view;
        segment.enter = 0;
        segment.exit = 1;
        return clip(startX, offsetX, center.x, view.width / 2)
            && clip(startY, offsetY, center.y, view.height / 2)
            && clip(startZ, offsetZ, center.z, view.depth / 2);
    }

    function clip(start, offset, middle, half) {
        const low = middle - half;
        const high = middle + half;
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
                if (entity.view.id !== selfId && entity.view.isBlocking) {
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
            for (const entity of entities.values()) {
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

        // Fades the walls between the camera and the own player's head.
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
                    setFaded(entity, crosses(entity, eye.x, eye.y, eye.z, offsetX, offsetY, offsetZ));
                }
            }
        },
    };
}
