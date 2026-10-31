// Draws the objects of the view: walls as boxes, players as capsules. World coordinates are relative to an integer
// origin near the own player, so the GPU's 32-bit floats stay precise anywhere in the int range.
const REBASE_DISTANCE = 2048;
const TELEPORT_DISTANCE = 6;

export async function createEntities(scene, options) {
    const BABYLON = window.BABYLON;
    const entities = new Map();
    const targetMarkers = [];
    // Reused by every frame's animate, so the render loop does not allocate.
    const selfPosition = { x: 0, z: 0 };
    let originX = null;
    let originY = null;
    let selfId = null;

    const groundMaterial = new BABYLON.StandardMaterial('ground', scene);
    groundMaterial.diffuseColor = new BABYLON.Color3(0.37, 0.62, 0.29);
    groundMaterial.specularColor = BABYLON.Color3.Black();
    const ground = BABYLON.MeshBuilder.CreateGround('ground', { width: 600, height: 600 }, scene);
    ground.material = groundMaterial;
    ground.isPickable = false;

    const wallMaterial = new BABYLON.StandardMaterial('wall', scene);
    wallMaterial.diffuseColor = BABYLON.Color3.FromHexString('#8d8577');
    wallMaterial.specularColor = new BABYLON.Color3(0.05, 0.05, 0.05);

    const targetMaterial = new BABYLON.StandardMaterial('target', scene);
    targetMaterial.emissiveColor = new BABYLON.Color3(1, 0.88, 0.4);
    targetMaterial.disableLighting = true;
    targetMaterial.alpha = 0.35;

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
        return new BABYLON.Vector3(x - originX + view.width / 2, view.height / 2, y - originY + view.depth / 2);
    }

    function createMesh(view) {
        if (view.kind !== 'player') {
            const box = BABYLON.MeshBuilder.CreateBox(view.id, { width: view.width, height: view.height, depth: view.depth }, scene);
            box.material = wallMaterial;
            box.metadata = { kind: 'wall' };
            return box;
        }

        const material = new BABYLON.StandardMaterial(`${view.id}-material`, scene);
        material.diffuseColor = BABYLON.Color3.FromHexString(view.color);
        const body = BABYLON.MeshBuilder.CreateCapsule(view.id, { height: view.height, radius: Math.min(view.width, view.depth) * 0.4 }, scene);
        body.material = material;
        const nose = BABYLON.MeshBuilder.CreateBox(`${view.id}-nose`, { width: 0.4, height: 0.4, depth: 0.8 }, scene);
        nose.parent = body;
        nose.position = new BABYLON.Vector3(0, view.height * 0.3, view.width * 0.4);
        nose.material = material;
        body.metadata = { kind: 'player' };
        return body;
    }

    function place(entity, x, y) {
        entity.mesh.position = centerOf(entity.view, x, y);
        entity.tween = null;
    }

    function moveTo(entity, x, y) {
        const target = centerOf(entity.view, x, y);
        if (BABYLON.Vector3.Distance(entity.mesh.position, target) > TELEPORT_DISTANCE) {
            place(entity, x, y);
            return;
        }

        entity.tween = { from: entity.mesh.position.clone(), to: target, start: performance.now(), duration: options.stepIntervalMs };
    }

    function setFacing(entity, facing) {
        entity.mesh.rotation.y = facing * Math.PI / 4;
    }

    function disposeEntity(entity) {
        const material = entity.mesh.material;
        entity.mesh.dispose();
        // Every player has its own material; the wall material is shared.
        if (entity.view.kind === 'player') {
            material?.dispose();
        }
    }

    function hideTarget() {
        for (const marker of targetMarkers) {
            marker.setEnabled(false);
        }
    }

    function hasShapeChanged(previous, view) {
        return previous.kind !== view.kind || previous.width !== view.width || previous.depth !== view.depth
            || previous.height !== view.height || previous.color !== view.color;
    }

    return {
        upsert(view) {
            if (view.isSelf) {
                selfId = view.id;
                ensureOrigin(view.x, view.y);
            } else if (originX === null) {
                ensureOrigin(view.x, view.y);
            }

            let entity = entities.get(view.id);
            if (!entity || hasShapeChanged(entity.view, view)) {
                if (entity) {
                    disposeEntity(entity);
                }

                entity = { view, mesh: createMesh(view), tween: null };
                entities.set(view.id, entity);
                place(entity, view.x, view.y);
                setFacing(entity, view.facing);
                return;
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
        },

        remove(id) {
            const entity = entities.get(id);
            if (entity) {
                disposeEntity(entity);
                entities.delete(id);
            }

            if (id === selfId) {
                selfId = null;
                hideTarget();
            }
        },

        setSelf(x, y, facing, animate) {
            const entity = entities.get(selfId);
            if (!entity) {
                return;
            }

            entity.view = { ...entity.view, x, y, facing };
            ensureOrigin(x, y);
            if (animate) {
                moveTo(entity, x, y);
            } else {
                place(entity, x, y);
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
                const marker = BABYLON.MeshBuilder.CreateGround(`target-${targetMarkers.length}`, { width: 1, height: 1 }, scene);
                marker.material = targetMaterial;
                marker.isPickable = false;
                targetMarkers.push(marker);
            }

            targetMarkers.forEach((marker, index) => {
                const tile = tiles[index];
                const isVisible = Boolean(tile) && originX !== null && selfId !== null;
                marker.setEnabled(isVisible);
                if (isVisible) {
                    marker.position.copyFromFloats(tile.x - originX + 0.5, 0.03, tile.y - originY + 0.5);
                }
            });
        },

        animate(now) {
            for (const entity of entities.values()) {
                const tween = entity.tween;
                if (!tween) {
                    continue;
                }

                const progress = Math.min(1, (now - tween.start) / tween.duration);
                BABYLON.Vector3.LerpToRef(tween.from, tween.to, progress, entity.mesh.position);
                if (progress >= 1) {
                    entity.tween = null;
                }
            }

            const self = entities.get(selfId);
            if (!self) {
                return null;
            }

            // The ground follows in whole meters, so its grid stays on the tiles.
            ground.position.x = Math.round((self.mesh.position.x + originX) / 4) * 4 - originX;
            ground.position.z = Math.round((self.mesh.position.z + originY) / 4) * 4 - originY;
            selfPosition.x = self.mesh.position.x;
            selfPosition.z = self.mesh.position.z;
            return selfPosition;
        },

        afterCameraMoved() {
            // Task 14 fades the walls that hide the own player.
        },
    };
}
