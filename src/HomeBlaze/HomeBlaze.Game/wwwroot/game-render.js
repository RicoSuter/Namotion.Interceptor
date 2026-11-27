// Lights, shadows and the post-processing of the game's look: soft sun shadows that follow the camera target, ink outlines
// from one geometry buffer that corner shading shares, multisampling and a light sharpen; and the graphics settings with
// their presets and the frame cap that decide how much of it each frame costs.
const SKY_COLOR = '#f3e6d3';
const SUN_COLOR = '#fff1d6';
// The shadow map covers this many tiles around its center, so its resolution does not spread over the view radius. The
// center lies this many tiles north of the camera target, in the middle of the ground the camera sees.
const SHADOW_EXTENT = 120;
const SHADOW_CENTER_NORTH = 20;
const SUN_DISTANCE = 200;
const SHADOW_MAP_SIZE = 2048;
const MULTISAMPLES = 4;
const OUTLINE_STRENGTH = 0.55;
const OUTLINE_WIDTH = 1.5;
// The ink goes from warm brown at 0 to near black at 1.
const INK_DARKNESS = 0.6;

/**
 * The graphics settings of each preset: the render resolution in device pixels per CSS pixel, capped at the device's
 * ratio (0 renders at the device's ratio); the frame cap (0 renders every animation frame); the shadows ('off', 'hard'
 * edges or 'soft' edges); whether outlines draw; the resolution of the corner shading relative to the render (0 turns it
 * off); and whether multisampling, the light sharpen, the wind and the sightline fade run.
 */
export const GRAPHICS_PRESETS = Object.freeze({
    low: Object.freeze({ resolution: 1, frameCap: 30, shadows: 'hard', outlines: true, ambientOcclusion: 0, multisampling: true, sharpen: true, wind: true, fade: true }),
    medium: Object.freeze({ resolution: 1.5, frameCap: 60, shadows: 'soft', outlines: true, ambientOcclusion: 0.5, multisampling: true, sharpen: true, wind: true, fade: true }),
    high: Object.freeze({ resolution: 2, frameCap: 60, shadows: 'soft', outlines: true, ambientOcclusion: 1, multisampling: true, sharpen: true, wind: true, fade: true }),
});
// The values each setting takes; a value outside them falls back to the value it replaces.
const SETTING_VALUES = Object.freeze({
    resolution: [1, 1.5, 2, 0],
    frameCap: [30, 60, 0],
    shadows: ['off', 'hard', 'soft'],
    outlines: [true, false],
    ambientOcclusion: [0, 0.5, 1],
    multisampling: [true, false],
    sharpen: [true, false],
    wind: [true, false],
    fade: [true, false],
});
const AUTO = 'auto';
const CUSTOM = 'custom';
const UNFOCUSED_FRAME_CAP = 10;
// A frame may come this much earlier than the cap's interval, so that a 60 Hz display does not skip every other frame
// at a cap of 60 for the jitter of its timestamps.
const FRAME_CAP_TOLERANCE_MS = 2;
// Auto drops one level when the average interval between rendered frames stays this much longer than the cap's interval
// for this long. The average smooths the alternating intervals of a refresh rate that is no multiple of the cap.
const SLOW_FRAME_FACTOR = 1.5;
const SLOW_DURATION_MS = 5000;
const AVERAGE_WEIGHT = 0.1;

/**
 * Expands a graphics choice into the settings it applies; pure, so it runs without a scene. The choice is
 * { preset: 'auto' | 'low' | 'medium' | 'high' | 'custom', ...settings }: a preset name takes that preset's settings and
 * ignores the rest, 'custom' merges the settings it names into the current ones, and anything else is 'auto', which
 * takes the auto level's preset. Custom settings that equal a preset report that preset. Returns
 * { preset, level, settings }, where level is the preset in effect or 'custom'.
 */
export function expandGraphics(choice, current, autoLevel) {
    const preset = choice?.preset;
    if (preset !== CUSTOM) {
        const level = Object.hasOwn(GRAPHICS_PRESETS, preset ?? '') ? preset : autoLevel;
        return { preset: level === preset ? preset : AUTO, level, settings: GRAPHICS_PRESETS[level] };
    }

    const base = current ?? GRAPHICS_PRESETS.medium;
    const settings = {};
    for (const [name, values] of Object.entries(SETTING_VALUES)) {
        settings[name] = values.includes(choice[name]) ? choice[name] : base[name];
    }

    for (const [name, presetSettings] of Object.entries(GRAPHICS_PRESETS)) {
        if (Object.keys(SETTING_VALUES).every(key => presetSettings[key] === settings[key])) {
            return { preset: name, level: name, settings: presetSettings };
        }
    }

    return { preset: CUSTOM, level: CUSTOM, settings: Object.freeze(settings) };
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

// The outline and the corner shading read one channel of depth and three of normals. Depth keeps the 32-bit float that
// Babylon picks where the device can render and filter it, and normals take half floats, which takes less than half the
// bytes per pixel of the buffer at the same look. WebGL1 has no one-channel format, and a device without renderable,
// filterable half floats keeps Babylon's defaults too.
function getGeometryFormats(engine) {
    const BABYLON = window.BABYLON;
    const caps = engine.getCaps();
    if (engine.webGLVersion < 2 || !caps.textureHalfFloatRender || !caps.textureHalfFloatLinearFiltering) {
        return undefined;
    }

    const depthType = caps.textureFloatRender && caps.textureFloatLinearFiltering
        ? BABYLON.Constants.TEXTURETYPE_FLOAT
        : BABYLON.Constants.TEXTURETYPE_HALF_FLOAT;
    return {
        [BABYLON.GeometryBufferRenderer.DEPTH_TEXTURE_TYPE]: { textureType: depthType, textureFormat: BABYLON.Constants.TEXTUREFORMAT_R },
        [BABYLON.GeometryBufferRenderer.NORMAL_TEXTURE_TYPE]: { textureType: BABYLON.Constants.TEXTURETYPE_HALF_FLOAT, textureFormat: BABYLON.Constants.TEXTUREFORMAT_RGBA },
    };
}

/**
 * Sets up the look for the camera's scene at the graphics choice, as expandGraphics takes it. Wind and the sightline fade
 * are settings the renderer reports but does not apply, since the entities draw them.
 */
export function createRenderer(scene, camera, choice) {
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

    let autoLevel = matchMedia('(pointer: coarse)').matches ? 'low' : 'medium';

    const shadows = new BABYLON.ShadowGenerator(SHADOW_MAP_SIZE, sun);
    shadows.usePercentageCloserFiltering = true;
    const shadowFilters = { hard: BABYLON.ShadowGenerator.QUALITY_LOW, soft: BABYLON.ShadowGenerator.QUALITY_MEDIUM };
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

    const geometry = scene.enableGeometryBufferRenderer(1, BABYLON.Constants.TEXTUREFORMAT_DEPTH16, getGeometryFormats(scene.getEngine()));
    const depthIndex = geometry.getTextureIndex(BABYLON.GeometryBufferRenderer.DEPTH_TEXTURE_TYPE);
    const normalIndex = geometry.getTextureIndex(BABYLON.GeometryBufferRenderer.NORMAL_TEXTURE_TYPE);

    BABYLON.Effect.ShadersStore.gameOutlineFragmentShader = OUTLINE_SHADER;
    // Created first, so it is the first pass of the camera's chain; detached, it leaves its slot empty for its return.
    const outline = new BABYLON.PostProcess('outline', 'gameOutline', ['texel', 'thickness', 'strength', 'inkColor'],
        ['depthSampler', 'normalSampler'], 1.0, camera);
    let isOutlineAttached = true;
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
    const buildPipelines = (ratio, isSharpened) => {
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
        pipeline.sharpenEnabled = isSharpened;
        pipeline.sharpen.edgeAmount = 0.35;
        pipeline.imageProcessingEnabled = true;
        pipeline.imageProcessing.contrast = 1.1;
        pipeline.imageProcessing.exposure = 1.05;
    };

    // The first pass of the camera's chain receives the scene, so its samples multisample the scene itself. Babylon keeps
    // the chain in a private list, with empty slots where passes were detached.
    let multisampledPass = null;
    const applyMultisampling = isMultisampled => {
        const first = camera._postProcesses.find(Boolean) ?? null;
        if (multisampledPass && multisampledPass !== first && camera._postProcesses.includes(multisampledPass)) {
            multisampledPass.samples = 1;
        }

        multisampledPass = first;
        const samples = isMultisampled ? MULTISAMPLES : 1;
        if (first && first.samples !== samples) {
            first.samples = samples;
        }
    };

    // Only the outlined meshes among those the camera sees draw into the geometry buffer, so labels, markers and faded
    // meshes get no outline; a faded mesh would also overwrite the depth and normals of what stands behind it. The list
    // is reused by every frame, and the buffer's render target is hooked again whenever the buffer recreates it. Without
    // outlines and corner shading, the buffer does not render.
    const outlined = [];
    let hookedBuffer = null;
    let isGeometryRendered = true;
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
    const applyGeometryRendering = buffer => {
        // A refresh rate of 0 renders the target once and then never again.
        const refreshRate = isGeometryRendered ? 1 : 0;
        if (buffer.refreshRate !== refreshRate) {
            buffer.refreshRate = refreshRate;
            buffer.resetRefreshCounter();
        }
    };
    scene.onBeforeRenderObservable.add(() => {
        const buffer = geometry.getGBuffer();
        if (buffer !== hookedBuffer) {
            hookedBuffer = buffer;
            buffer.getCustomRenderList = selectOutlined;
            applyGeometryRendering(buffer);
        }
    });

    const engine = scene.getEngine();
    const canvas = engine.getRenderingCanvas();
    const pacer = createFramePacer();
    let graphics = null;
    let slowSince = 0;

    // The engine does not adapt to the device pixel ratio, so the resolution follows it here: on a settings change, a
    // resize, and a change of the ratio alone, as when the window moves to another display.
    const applyResolution = () => {
        const pixelRatio = window.devicePixelRatio || 1;
        const level = 1 / Math.min(pixelRatio, graphics.settings.resolution || pixelRatio);
        if (engine.getHardwareScalingLevel() !== level) {
            engine.setHardwareScalingLevel(level);
        }
    };
    let pixelRatioQuery = null;
    const onPixelRatioChange = () => {
        pixelRatioQuery?.removeEventListener('change', onPixelRatioChange);
        pixelRatioQuery = matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
        pixelRatioQuery.addEventListener('change', onPixelRatioChange);
        if (graphics) {
            applyResolution();
        }
    };
    onPixelRatioChange();

    // Every setting of the look changes here, so that a preset and a single setting take the same path. Only what differs
    // from the settings in effect is touched, because most changes recompile shaders or recreate render targets.
    const applyGraphics = next => {
        const settings = next.settings;
        graphics = next;
        slowSince = 0;
        applyResolution();

        const isShadowed = settings.shadows !== 'off';
        if (sun.shadowEnabled !== isShadowed) {
            sun.shadowEnabled = isShadowed;
        }

        if (isShadowed && shadows.filteringQuality !== shadowFilters[settings.shadows]) {
            shadows.filteringQuality = shadowFilters[settings.shadows];
        }

        if (isOutlineAttached !== settings.outlines) {
            isOutlineAttached = settings.outlines;
            if (settings.outlines) {
                camera.attachPostProcess(outline, 0);
            } else {
                camera.detachPostProcess(outline);
            }
        }

        buildPipelines(settings.ambientOcclusion, settings.sharpen);
        if (pipeline.sharpenEnabled !== settings.sharpen) {
            pipeline.sharpenEnabled = settings.sharpen;
        }

        isGeometryRendered = settings.outlines || settings.ambientOcclusion > 0;
        applyGeometryRendering(geometry.getGBuffer());
        applyMultisampling(settings.multisampling);

        if (canvas) {
            const data = canvas.dataset;
            data.gamePreset = next.level;
            data.gameResolution = String(settings.resolution);
            data.gameFrameCap = String(settings.frameCap);
            data.gameShadows = settings.shadows;
            data.gameOutlines = String(settings.outlines);
            data.gameAmbientOcclusion = String(settings.ambientOcclusion);
            data.gameMultisampling = String(settings.multisampling);
            data.gameSharpen = String(settings.sharpen);
            data.gameWind = String(settings.wind);
            data.gameFade = String(settings.fade);
        }
    };

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
            applyGraphics(expandGraphics({ preset: AUTO }, null, autoLevel));
        }
    };

    const api = {
        sun,
        shadowGenerator: shadows,
        geometryBuffer: geometry,
        /**
         * Applies the graphics choice, as expandGraphics takes it, merging custom settings into those in effect; returns the
         * graphics in effect, as getGraphics does.
         */
        setGraphics(next) {
            applyGraphics(expandGraphics(next, graphics?.settings, autoLevel));
            return api.getGraphics();
        },
        /** The graphics in effect: { preset, level, ...settings }. */
        getGraphics() {
            return { preset: graphics.preset, level: graphics.level, ...graphics.settings };
        },
        /** Whether the frame of this animation frame time renders, by the frame cap; call once per animation frame. */
        shouldRender(now, isFocused) {
            const cap = isFocused ? graphics.settings.frameCap || Infinity : UNFOCUSED_FRAME_CAP;
            if (!pacer.tryRender(now, cap)) {
                return false;
            }

            // Auto stops adjusting once a setting was changed, which makes the graphics custom.
            if (graphics.preset === AUTO && isFocused) {
                trackAuto(cap, now);
            }

            return true;
        },
        /** Renders the next frame at once and restarts the frame timing, so the pause does not count as slow. */
        resume() {
            pacer.reset();
            slowSince = 0;
        },
        /** Keeps the resolution setting after the canvas resized. */
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
    api.setGraphics(choice);
    return api;
}
