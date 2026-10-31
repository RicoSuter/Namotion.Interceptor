import { createEntities } from './game-entities.js';
import { createInput } from './game-input.js';

// The pages of a tab share this module, so each one owns its view by the id it passes.
const views = new Map();
const NOT_STARTED = { isStarted: false, error: null };
// A larger jump of the own player, such as a teleport or a rebase of the world origin, moves the camera at once.
const CAMERA_SNAP_DISTANCE = 20;
// Where the camera sits relative to the point it looks at, in tiles: above it and south of it.
const CAMERA_HEIGHT = 28;
const CAMERA_DISTANCE = 30;
let babylonLoading = null;

// The page also hosts an AMD loader with a global define, which would make the UMD builds register as anonymous AMD
// modules instead of setting window.BABYLON. Running them with define shadowed by a parameter keeps them on the global
// without touching the loader that other scripts use meanwhile.
async function loadGlobalScript(source) {
    let response;
    try {
        response = await fetch(source);
    } catch (error) {
        throw new Error(`Loading ${source} failed: ${error.message}`);
    }

    if (!response.ok) {
        throw new Error(`Loading ${source} failed with status ${response.status}.`);
    }

    const code = `(function (define) {\n${await response.text()}\n}).call(globalThis);\n//# sourceURL=${source}`;
    const url = URL.createObjectURL(new Blob([code], { type: 'text/javascript' }));
    try {
        await new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = url;
            script.onload = () => resolve();
            script.onerror = () => reject(new Error(`Loading ${source} failed.`));
            document.head.appendChild(script);
        });
    } finally {
        URL.revokeObjectURL(url);
    }
}

async function ensureBabylon(baseUrl) {
    // Shared by every start, so a second page in the same tab does not load the scripts again.
    babylonLoading ??= loadGlobalScript(`${baseUrl}lib/babylonjs/babylon.js`)
        .then(() => loadGlobalScript(`${baseUrl}lib/babylonjs/babylonjs.loaders.min.js`))
        .then(() => {
            if (!window.BABYLON?.Engine) {
                throw new Error('Babylon.js did not initialize.');
            }
        });
    await babylonLoading;
}

function hasWebGl() {
    const canvas = document.createElement('canvas');
    const context = canvas.getContext('webgl2') || canvas.getContext('webgl');
    // Browsers cap the live contexts per page, so the probe gives its own back right away.
    context?.getExtension('WEBGL_lose_context')?.loseContext();
    return Boolean(context);
}

/** Starts the view with the id in options.viewId; a dispose of that id while starting releases it and returns not started without error. */
export async function start(canvas, dotNet, options) {
    const viewId = options.viewId;
    dispose(viewId);
    const handle = { view: null, isDisposed: false };
    views.set(viewId, handle);
    const result = await startView(handle, canvas, dotNet, options);
    if (!result.isStarted && views.get(viewId) === handle) {
        views.delete(viewId);
    }

    return result;
}

async function startView(handle, canvas, dotNet, options) {
    if (!hasWebGl()) {
        return { isStarted: false, error: 'This browser cannot show the game because WebGL is not available.' };
    }

    try {
        await ensureBabylon(options.baseUrl);
    } catch (error) {
        babylonLoading = null;
        return { isStarted: false, error: error.message };
    }

    if (handle.isDisposed) {
        return NOT_STARTED;
    }

    const view = new GameView(canvas, dotNet, options);
    try {
        await view.initialize();
    } catch (error) {
        view.dispose();
        return { isStarted: false, error: `The game could not be loaded: ${error.message}` };
    }

    if (handle.isDisposed) {
        view.dispose();
        return NOT_STARTED;
    }

    handle.view = view;
    return { isStarted: true, error: null };
}

export function applyUpdate(viewId, update) {
    views.get(viewId)?.view?.applyUpdate(update);
}

export function dispose(viewId) {
    const handle = views.get(viewId);
    if (handle) {
        views.delete(viewId);
        handle.isDisposed = true;
        handle.view?.dispose();
    }
}

class GameView {
    constructor(canvas, dotNet, options) {
        this.canvas = canvas;
        this.dotNet = dotNet;
        this.options = options;
        this.lastFrame = performance.now();
    }

    async initialize() {
        const BABYLON = window.BABYLON;
        this.engine = new BABYLON.Engine(this.canvas, true, { stencil: true, loseContextOnDispose: true }, true);
        this.cameraTarget = new BABYLON.Vector3();
        this.desiredCameraTarget = new BABYLON.Vector3();
        this.hasCameraTarget = false;
        this.scene = new BABYLON.Scene(this.engine);
        const sky = new BABYLON.Color3(0.78, 0.86, 0.93);
        this.scene.clearColor = new BABYLON.Color4(sky.r, sky.g, sky.b, 1);
        this.scene.fogMode = BABYLON.Scene.FOGMODE_LINEAR;
        this.scene.fogColor = sky;
        this.scene.fogStart = 110;
        this.scene.fogEnd = 230;

        // One world unit is one tile of 25 cm; x grows to the east, z to the north, y up.
        this.camera = new BABYLON.FreeCamera('camera', new BABYLON.Vector3(0, CAMERA_HEIGHT + 3, -CAMERA_DISTANCE), this.scene);
        this.camera.fov = 0.8;
        this.camera.minZ = 0.5;
        this.camera.maxZ = 500;
        this.camera.setTarget(BABYLON.Vector3.Zero());

        const skyLight = new BABYLON.HemisphericLight('sky', new BABYLON.Vector3(0.2, 1, -0.3), this.scene);
        skyLight.intensity = 0.65;
        skyLight.groundColor = new BABYLON.Color3(0.35, 0.4, 0.3);
        const sun = new BABYLON.DirectionalLight('sun', new BABYLON.Vector3(-0.5, -1, 0.7), this.scene);
        sun.intensity = 0.75;

        // The canvas carries the part and model counts for tests and diagnosis.
        this.entities = await createEntities(this.scene, { ...this.options, statsElement: this.canvas });
        this.input = createInput(this.dotNet, this.options, this.entities);

        this.engine.runRenderLoop(() => this.render());
        this.resizeObserver = new ResizeObserver(() => this.engine.resize());
        this.resizeObserver.observe(this.canvas);
    }

    applyUpdate(update) {
        // A lost own player drops the input's prediction, so a later own player starts from the server's position.
        for (const id of update.removed) {
            if (this.entities.remove(id)) {
                this.input.reset();
            }
        }

        for (const view of update.upserted) {
            if (this.entities.upsert(view)) {
                this.input.reset();
            }

            if (view.isSelf) {
                this.input.onServerSelf(view);
            }
        }
    }

    render() {
        const BABYLON = window.BABYLON;
        const now = performance.now();
        const elapsedSeconds = Math.min(0.1, (now - this.lastFrame) / 1000);
        this.lastFrame = now;

        const self = this.entities.animate(now);
        if (self) {
            const desired = this.desiredCameraTarget.copyFromFloats(self.x, 3, self.z);
            if (this.hasCameraTarget && BABYLON.Vector3.DistanceSquared(this.cameraTarget, desired) <= CAMERA_SNAP_DISTANCE * CAMERA_SNAP_DISTANCE) {
                BABYLON.Vector3.LerpToRef(this.cameraTarget, desired, 1 - Math.exp(-elapsedSeconds * 10), this.cameraTarget);
            } else {
                this.cameraTarget.copyFrom(desired);
                this.hasCameraTarget = true;
            }

            this.camera.position.copyFromFloats(this.cameraTarget.x, this.cameraTarget.y + CAMERA_HEIGHT, this.cameraTarget.z - CAMERA_DISTANCE);
            this.camera.setTarget(this.cameraTarget);
            this.entities.afterCameraMoved(this.camera, now);
        }

        this.scene.render();
    }

    dispose() {
        this.input?.dispose();
        this.resizeObserver?.disconnect();
        this.engine?.stopRenderLoop();
        this.entities?.dispose();
        this.scene?.dispose();
        this.engine?.dispose();
    }
}
