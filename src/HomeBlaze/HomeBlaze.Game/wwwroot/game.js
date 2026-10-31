import { createEntities } from './game-entities.js';
import { createInput } from './game-input.js';

let current = null;
let babylonLoading = null;

// The page also hosts an AMD loader with a global define, which would make the UMD builds register as anonymous AMD
// modules instead of setting window.BABYLON. Running them with define shadowed by a parameter keeps them on the global
// without touching the loader that other scripts use meanwhile.
async function loadGlobalScript(source) {
    const response = await fetch(source);
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
    return Boolean(canvas.getContext('webgl2') || canvas.getContext('webgl'));
}

export async function start(canvas, dotNet, options) {
    dispose();
    if (!hasWebGl()) {
        return { isStarted: false, error: 'This browser cannot show the game because WebGL is not available.' };
    }

    try {
        await ensureBabylon(options.baseUrl);
    } catch (error) {
        babylonLoading = null;
        return { isStarted: false, error: error.message };
    }

    const view = new GameView(canvas, dotNet, options);
    try {
        await view.initialize();
    } catch (error) {
        view.dispose();
        return { isStarted: false, error: `The game could not be loaded: ${error.message}` };
    }

    current = view;
    return { isStarted: true, error: null };
}

export function applyUpdate(update) {
    current?.applyUpdate(update);
}

export function dispose() {
    current?.dispose();
    current = null;
}

class GameView {
    constructor(canvas, dotNet, options) {
        this.canvas = canvas;
        this.dotNet = dotNet;
        this.options = options;
        this.cameraTarget = null;
        this.lastFrame = performance.now();
    }

    async initialize() {
        const BABYLON = window.BABYLON;
        this.engine = new BABYLON.Engine(this.canvas, true, { stencil: true }, true);
        this.scene = new BABYLON.Scene(this.engine);
        const sky = new BABYLON.Color3(0.78, 0.86, 0.93);
        this.scene.clearColor = new BABYLON.Color4(sky.r, sky.g, sky.b, 1);
        this.scene.fogMode = BABYLON.Scene.FOGMODE_LINEAR;
        this.scene.fogColor = sky;
        this.scene.fogStart = 90;
        this.scene.fogEnd = 200;

        // One world unit is one tile of 25 cm; x grows to the east, z to the north, y up.
        this.camera = new BABYLON.FreeCamera('camera', new BABYLON.Vector3(0, 22, -20), this.scene);
        this.camera.fov = 0.8;
        this.camera.minZ = 0.5;
        this.camera.maxZ = 450;
        this.camera.setTarget(BABYLON.Vector3.Zero());

        const skyLight = new BABYLON.HemisphericLight('sky', new BABYLON.Vector3(0.2, 1, -0.3), this.scene);
        skyLight.intensity = 0.65;
        skyLight.groundColor = new BABYLON.Color3(0.35, 0.4, 0.3);
        const sun = new BABYLON.DirectionalLight('sun', new BABYLON.Vector3(-0.5, -1, 0.7), this.scene);
        sun.intensity = 0.75;

        this.entities = await createEntities(this.scene, this.options);
        this.input = createInput(this.dotNet, this.options, this.entities);

        this.engine.runRenderLoop(() => this.render());
        this.resizeObserver = new ResizeObserver(() => this.engine.resize());
        this.resizeObserver.observe(this.canvas);
    }

    applyUpdate(update) {
        for (const id of update.removed) {
            this.entities.remove(id);
        }

        for (const view of update.upserted) {
            this.entities.upsert(view);
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
            const desired = new BABYLON.Vector3(self.x, 3, self.z);
            this.cameraTarget = this.cameraTarget
                ? BABYLON.Vector3.Lerp(this.cameraTarget, desired, 1 - Math.exp(-elapsedSeconds * 10))
                : desired;
            this.camera.position.copyFromFloats(this.cameraTarget.x, this.cameraTarget.y + 19, this.cameraTarget.z - 20);
            this.camera.setTarget(this.cameraTarget);
            this.entities.afterCameraMoved(this.camera, now);
        }

        this.scene.render();
    }

    dispose() {
        this.input?.dispose();
        this.resizeObserver?.disconnect();
        this.engine?.stopRenderLoop();
        this.scene?.dispose();
        this.engine?.dispose();
    }
}
