// Lights, shadows and the post-processing of the game's look: soft sun shadows that follow the camera target, ink outlines
// from one geometry buffer that corner shading shares, multisampling and a light sharpen; and the quality presets with the
// frame cap that decide how much of it each frame costs.
const SKY_COLOR = '#f3e6d3';
const SUN_COLOR = '#fff1d6';
const SHADOW_MAP_SIZE = 2048;
// The shadow map covers this many tiles around its center, so its resolution does not spread over the view radius. The
// center lies this many tiles north of the camera target, in the middle of the ground the camera sees.
const SHADOW_EXTENT = 120;
const SHADOW_CENTER_NORTH = 20;
const SUN_DISTANCE = 200;
const OUTLINE_STRENGTH = 0.55;
const OUTLINE_WIDTH = 1.5;
// The ink goes from warm brown at 0 to near black at 1.
const INK_DARKNESS = 0.6;

/**
 * The render resolution in device pixels per CSS pixel (capped at the device's ratio), the frame cap, and the resolution of
 * the corner shading relative to the render (0 turns it off). Outlines, shadows and multisampling stay on in every preset.
 */
export const QUALITY_PRESETS = {
    low: { resolution: 1, frameCap: 30, ambientOcclusion: 0 },
    medium: { resolution: 1.5, frameCap: 60, ambientOcclusion: 0.5 },
    high: { resolution: 2, frameCap: 60, ambientOcclusion: 1 },
};
const AUTO = 'auto';
const QUALITY_KEY = 'homeblaze.game.quality';
const UNFOCUSED_FRAME_CAP = 10;
// A frame may come this much earlier than the cap's interval, so that a 60 Hz display does not skip every other frame
// at a cap of 60 for the jitter of its timestamps.
const FRAME_CAP_TOLERANCE_MS = 2;
// Auto drops one level when the average interval between rendered frames stays this much longer than the cap's interval
// for this long. The average smooths the alternating intervals of a refresh rate that is no multiple of the cap.
const SLOW_FRAME_FACTOR = 1.5;
const SLOW_DURATION_MS = 5000;
const AVERAGE_WEIGHT = 0.1;

/** The stored quality choice of this browser: a preset name or 'auto', the default. */
export function readQuality() {
    // Storage can be missing or throw, as in private windows; the page then uses Auto.
    try {
        const value = localStorage.getItem(QUALITY_KEY);
        return Object.hasOwn(QUALITY_PRESETS, value ?? '') ? value : AUTO;
    } catch {
        return AUTO;
    }
}

/** Stores the quality choice for this browser; without storage it holds for the page only. */
export function writeQuality(value) {
    try {
        localStorage.setItem(QUALITY_KEY, value);
    } catch {
        // Kept for this page only.
    }
}

const OUTLINE_SHADER = `
    precision highp float;
    varying vec2 vUV;
    uniform sampler2D textureSampler;
    uniform sampler2D depthSampler;
    uniform sampler2D normalSampler;
    uniform vec2 texel;
    uniform float thickness;
    uniform float strength;
    uniform vec3 inkColor;
    float depthAt(vec2 uv) { return texture2D(depthSampler, uv).r; }
    vec3 normalAt(vec2 uv) { return texture2D(normalSampler, uv).rgb; }
    void main(void) {
        vec3 color = texture2D(textureSampler, vUV).rgb;
        vec2 o = texel * thickness;
        float d = depthAt(vUV);
        float depthEdge = (abs(depthAt(vUV - vec2(o.x, 0.0)) - d) + abs(depthAt(vUV + vec2(o.x, 0.0)) - d)
            + abs(depthAt(vUV + vec2(0.0, o.y)) - d) + abs(depthAt(vUV - vec2(0.0, o.y)) - d)) / max(d, 0.0001);
        vec3 n = normalAt(vUV);
        float normalEdge = (1.0 - dot(n, normalAt(vUV - vec2(o.x, 0.0)))) + (1.0 - dot(n, normalAt(vUV + vec2(0.0, o.y))));
        float edge = max(smoothstep(0.02, 0.05, depthEdge), smoothstep(0.2, 0.45, normalEdge));
        if (d <= 0.0) edge = 0.0;
        gl_FragColor = vec4(mix(color, inkColor, edge * strength), 1.0);
    }`;

/**
 * Paces animation frames to a frame cap and averages the interval between the frames it lets render; pure, so it runs
 * without a scene.
 */
export function createFramePacer() {
    let lastSlot = -Infinity;
    let lastRendered = -Infinity;
    let lastCap = 0;
    const pacer = {
        /** The average interval between rendered frames at the current cap in milliseconds, or 0 before two frames. */
        averageInterval: 0,
        /** Whether the animation frame at the time renders under the cap. */
        tryRender(now, cap) {
            const interval = 1000 / cap;
            const elapsed = now - lastSlot;
            if (elapsed < interval - FRAME_CAP_TOLERANCE_MS) {
                return false;
            }

            // Carries the remainder, so that a refresh rate that is no multiple of the cap still averages the cap; after a
            // stall the slots start over.
            lastSlot = elapsed < 2 * interval ? lastSlot + interval : now;
            if (cap !== lastCap) {
                lastCap = cap;
                pacer.averageInterval = 0;
            } else if (lastRendered > -Infinity) {
                const frameTime = now - lastRendered;
                pacer.averageInterval = pacer.averageInterval === 0
                    ? frameTime
                    : pacer.averageInterval + (frameTime - pacer.averageInterval) * AVERAGE_WEIGHT;
            }

            lastRendered = now;
            return true;
        },
        /** Lets the next frame render at once and starts the average over, as after a pause. */
        reset() {
            lastSlot = -Infinity;
            lastRendered = -Infinity;
            pacer.averageInterval = 0;
        },
    };
    return pacer;
}

/** Sets up the look for the camera's scene at the quality choice, a preset name or 'auto'. */
export function createRenderer(scene, camera, quality = AUTO) {
    const BABYLON = window.BABYLON;
    const sky = BABYLON.Color3.FromHexString(SKY_COLOR);
    scene.clearColor = new BABYLON.Color4(sky.r, sky.g, sky.b, 1);
    scene.fogColor = sky;

    const hemisphere = new BABYLON.HemisphericLight('sky', new BABYLON.Vector3(0.2, 1, -0.3), scene);
    hemisphere.intensity = 0.55;
    hemisphere.groundColor = new BABYLON.Color3(0.35, 0.4, 0.3);
    const sun = new BABYLON.DirectionalLight('sun', new BABYLON.Vector3(-0.5, -1, 0.7).normalize(), scene);
    sun.intensity = 1.0;
    sun.diffuse = BABYLON.Color3.FromHexString(SUN_COLOR);
    sun.shadowFrustumSize = SHADOW_EXTENT;
    sun.shadowMinZ = 1;
    sun.shadowMaxZ = SUN_DISTANCE * 2;

    const shadows = new BABYLON.ShadowGenerator(SHADOW_MAP_SIZE, sun);
    shadows.usePercentageCloserFiltering = true;
    shadows.filteringQuality = BABYLON.ShadowGenerator.QUALITY_MEDIUM;
    shadows.darkness = 0.35;
    shadows.bias = 0.002;
    // A faded wall keeps its shadow.
    shadows.transparencyShadow = true;

    // The axes of the shadow map in world space, to which follow snaps the sun, so that shadow edges do not shimmer while
    // the camera glides.
    const shadowTexel = SHADOW_EXTENT / SHADOW_MAP_SIZE;
    const shadowRight = BABYLON.Vector3.Cross(BABYLON.Vector3.Up(), sun.direction).normalize();
    const shadowUp = BABYLON.Vector3.Cross(sun.direction, shadowRight).normalize();
    const shadowCenter = new BABYLON.Vector3();

    const geometry = scene.enableGeometryBufferRenderer();
    const depthIndex = geometry.getTextureIndex(BABYLON.GeometryBufferRenderer.DEPTH_TEXTURE_TYPE);
    const normalIndex = geometry.getTextureIndex(BABYLON.GeometryBufferRenderer.NORMAL_TEXTURE_TYPE);

    BABYLON.Effect.ShadersStore.gameOutlineFragmentShader = OUTLINE_SHADER;
    const outline = new BABYLON.PostProcess('outline', 'gameOutline', ['texel', 'thickness', 'strength', 'inkColor'],
        ['depthSampler', 'normalSampler'], 1.0, camera);
    // The first pass of the chain receives the scene, so this multisamples the scene itself.
    outline.samples = 4;
    const ink = [0.25 + (0.06 - 0.25) * INK_DARKNESS, 0.16 + (0.05 - 0.16) * INK_DARKNESS, 0.12 + (0.05 - 0.12) * INK_DARKNESS];
    outline.onApply = effect => {
        const buffer = geometry.getGBuffer();
        effect.setFloat2('texel', 1 / outline.width, 1 / outline.height);
        effect.setFloat('thickness', OUTLINE_WIDTH);
        effect.setFloat('strength', OUTLINE_STRENGTH);
        effect.setFloat3('inkColor', ink[0], ink[1], ink[2]);
        effect.setTexture('depthSampler', buffer.textures[depthIndex]);
        effect.setTexture('normalSampler', buffer.textures[normalIndex]);
    };

    let ambientOcclusion = null;
    let ambientOcclusionRatio = -1;
    let pipeline = null;
    // Babylon reads the ratio of the corner shading only when it creates the pipeline, and appends the passes of a pipeline
    // to the camera's chain, so a new ratio rebuilds the corner shading and the sharpen and image processing after it.
    const buildPipelines = ratio => {
        if (ratio === ambientOcclusionRatio) {
            return;
        }

        ambientOcclusionRatio = ratio;
        // Without the argument true, which would also disable the scene's geometry buffer that the outline reads.
        ambientOcclusion?.dispose();
        pipeline?.dispose();
        ambientOcclusion = null;
        if (ratio > 0) {
            // Forced onto the geometry buffer, so it does not switch the scene to a prepass the outline cannot read.
            ambientOcclusion = new BABYLON.SSAO2RenderingPipeline('ssao', scene, { ssaoRatio: ratio, blurRatio: ratio }, [camera], true);
            ambientOcclusion.radius = 3;
            ambientOcclusion.totalStrength = 1.3;
            ambientOcclusion.base = 0.15;
            ambientOcclusion.samples = 8;
            ambientOcclusion.maxZ = 250;
        }

        pipeline = new BABYLON.DefaultRenderingPipeline('look', true, scene, [camera]);
        pipeline.fxaaEnabled = false;
        pipeline.sharpenEnabled = true;
        pipeline.sharpen.edgeAmount = 0.35;
        pipeline.imageProcessingEnabled = true;
        pipeline.imageProcessing.contrast = 1.1;
        pipeline.imageProcessing.exposure = 1.05;
    };

    // Only the outlined meshes among those the camera sees draw into the geometry buffer, so labels, markers and faded
    // meshes get no outline; a faded mesh would also overwrite the depth and normals of what stands behind it. The list
    // is reused by every frame, and the buffer's render target is hooked again whenever the buffer recreates it.
    const outlined = [];
    let hookedBuffer = null;
    const selectOutlined = (passIndex, activeMeshes, count) => {
        outlined.length = 0;
        for (let index = 0; index < count; index++) {
            const mesh = activeMeshes[index];
            if (mesh.gameOutlined) {
                outlined.push(mesh);
            }
        }

        return outlined;
    };
    scene.onBeforeRenderObservable.add(() => {
        const buffer = geometry.getGBuffer();
        if (buffer !== hookedBuffer) {
            hookedBuffer = buffer;
            buffer.getCustomRenderList = selectOutlined;
        }
    });

    const engine = scene.getEngine();
    const canvas = engine.getRenderingCanvas();
    const pacer = createFramePacer();
    let current = null;
    let autoLevel = matchMedia('(pointer: coarse)').matches ? 'low' : 'medium';
    let slowSince = 0;

    // The engine does not adapt to the device pixel ratio, so the resolution follows it here: on a preset change, a
    // resize, and a change of the ratio alone, as when the window moves to another display.
    const applyResolution = () => {
        const level = 1 / Math.min(window.devicePixelRatio || 1, current.resolution);
        if (engine.getHardwareScalingLevel() !== level) {
            engine.setHardwareScalingLevel(level);
        }
    };
    let pixelRatioQuery = null;
    const onPixelRatioChange = () => {
        pixelRatioQuery?.removeEventListener('change', onPixelRatioChange);
        pixelRatioQuery = matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
        pixelRatioQuery.addEventListener('change', onPixelRatioChange);
        if (current) {
            applyResolution();
        }
    };
    onPixelRatioChange();

    // Steps down at most to low and never back up, so a level that was too slow does not come back.
    const trackAuto = (cap, now) => {
        if (pacer.averageInterval <= (1000 / cap) * SLOW_FRAME_FACTOR) {
            slowSince = 0;
            return;
        }

        slowSince ||= now;
        if (now - slowSince > SLOW_DURATION_MS && autoLevel !== 'low') {
            autoLevel = autoLevel === 'high' ? 'medium' : 'low';
            slowSince = 0;
            api.setQuality(AUTO);
        }
    };

    const api = {
        sun,
        geometryBuffer: geometry,
        /** Applies a preset name or 'auto', which starts low on touch devices and medium elsewhere; anything else is auto. */
        setQuality(choice) {
            const isPreset = Object.hasOwn(QUALITY_PRESETS, choice);
            const level = isPreset ? choice : autoLevel;
            const preset = QUALITY_PRESETS[level];
            current = { isAuto: !isPreset, ...preset };
            slowSince = 0;
            applyResolution();
            buildPipelines(preset.ambientOcclusion);
            if (canvas) {
                canvas.dataset.gameQuality = level;
                canvas.dataset.gameAmbientOcclusion = String(preset.ambientOcclusion);
            }
        },
        /** Whether the frame of this animation frame time renders, by the frame cap; call once per animation frame. */
        shouldRender(now, isFocused) {
            const cap = isFocused ? current.frameCap : UNFOCUSED_FRAME_CAP;
            if (!pacer.tryRender(now, cap)) {
                return false;
            }

            if (current.isAuto && isFocused) {
                trackAuto(cap, now);
            }

            return true;
        },
        /** Renders the next frame at once and restarts the frame timing, so the pause does not count as slow. */
        resume() {
            pacer.reset();
            slowSince = 0;
        },
        /** Keeps the preset's resolution after the canvas resized. */
        applyResolution() {
            applyResolution();
        },
        /** Stops following the device pixel ratio. */
        dispose() {
            pixelRatioQuery?.removeEventListener('change', onPixelRatioChange);
        },
        /** Lets the mesh cast and receive shadows and get outlines. */
        addMesh(mesh) {
            if (mesh.getTotalVertices() > 0) {
                shadows.addShadowCaster(mesh, false);
                mesh.receiveShadows = true;
                mesh.gameOutlined = true;
            }
        },
        /** Takes the mesh out of the geometry buffer while it is see-through, or puts it back. */
        setOutlined(mesh, isOutlined) {
            mesh.gameOutlined = isOutlined && mesh.getTotalVertices() > 0;
        },
        /** Lets the flat ground receive shadows and corner shading without casting shadows. */
        addGround(mesh) {
            mesh.receiveShadows = true;
            mesh.gameOutlined = true;
        },
        /** Keeps the shadow map on the ground the camera sees; call once per frame before rendering. */
        follow(target) {
            shadowCenter.copyFromFloats(target.x, 0, target.z + SHADOW_CENTER_NORTH);
            const right = BABYLON.Vector3.Dot(shadowCenter, shadowRight);
            const up = BABYLON.Vector3.Dot(shadowCenter, shadowUp);
            shadowRight.scaleAndAddToRef(Math.round(right / shadowTexel) * shadowTexel - right, shadowCenter);
            shadowUp.scaleAndAddToRef(Math.round(up / shadowTexel) * shadowTexel - up, shadowCenter);
            sun.direction.scaleToRef(-SUN_DISTANCE, sun.position);
            sun.position.addInPlace(shadowCenter);
        },
    };
    api.setQuality(quality);
    return api;
}
