// Converts the six Quaternius people from FBX to the GLB models and PNG previews under wwwroot/models/people/.
// Usage: PLAYWRIGHT_PACKAGE=<.playwright/package of a built test project> node convert-people.js <fbx-folder> <out-folder>
// See README.md in this folder.
const fs = require('fs');
const path = require('path');

const THREE_VERSION = '0.160.0';
const PREVIEW_SIZE = 128;
const PEOPLE = {
    Male_Casual: 'male-casual',
    Male_Shirt: 'male-shirt',
    Male_LongSleeve: 'male-long-sleeve',
    Female_Casual: 'female-casual',
    Female_Alternative: 'female-alternative',
    Female_TankTop: 'female-tank-top',
};

const [fbxFolder, outFolder] = process.argv.slice(2);
if (!fbxFolder || !outFolder || !process.env.PLAYWRIGHT_PACKAGE) {
    console.error('Usage: PLAYWRIGHT_PACKAGE=<path> node convert-people.js <fbx-folder> <out-folder>');
    process.exit(1);
}

const { chromium } = require(process.env.PLAYWRIGHT_PACKAGE);

const page = `<!doctype html>
<script type="importmap">{"imports":{
    "three":"https://cdn.jsdelivr.net/npm/three@${THREE_VERSION}/build/three.module.js",
    "three/addons/":"https://cdn.jsdelivr.net/npm/three@${THREE_VERSION}/examples/jsm/"}}</script>
<script type="module">
import * as THREE from 'three';
import { FBXLoader } from 'three/addons/loaders/FBXLoader.js';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';
import { mergeVertices } from 'three/addons/utils/BufferGeometryUtils.js';

// The game plays these clips by name; the source names them like "HumanArmature|Man_Idle".
const CLIPS = ['Idle', 'Walk', 'Clapping', 'Sitting', 'Jump'];

function toBase64(bytes) {
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }
    return btoa(binary);
}

// FBX gives Phong materials, which the exporter writes half metallic; the people are plain colored, so they become matte.
// The loader reads the FBX colors as sRGB and converts them to linear, but the files store linear values already, so
// converting back keeps the colors of the source instead of darkening them twice.
function toMatte(material) {
    return new THREE.MeshStandardMaterial({
        name: material.name,
        color: material.color.clone().convertLinearToSRGB(),
        metalness: 0,
        roughness: 1,
        flatShading: true,
    });
}

// The loader leaves one draw group per run of faces and duplicates every corner; welding the corners and sorting the
// faces by material leaves one primitive per material, which shrinks the file several times.
function compact(geometry) {
    const groups = geometry.groups.map(group => ({ ...group }));
    const welded = mergeVertices(geometry);
    const index = welded.index.array;
    const materialIndices = [...new Set(groups.map(group => group.materialIndex))].sort((a, b) => a - b);
    const sorted = [];
    welded.clearGroups();
    for (const materialIndex of materialIndices) {
        const start = sorted.length;
        for (const group of groups.filter(group => group.materialIndex === materialIndex)) {
            for (let i = group.start; i < group.start + group.count; i++) {
                sorted.push(index[i]);
            }
        }
        welded.addGroup(start, sorted.length - start, materialIndex);
    }
    welded.setIndex(sorted);
    return welded;
}

// Skin weights fit in normalized bytes and the 32 joints in byte indices, which glTF allows without an extension.
function quantizeSkin(geometry) {
    const weights = geometry.getAttribute('skinWeight');
    const indices = geometry.getAttribute('skinIndex');
    const quantizedWeights = new Uint8Array(weights.count * 4);
    const quantizedIndices = new Uint8Array(indices.count * 4);
    for (let vertex = 0; vertex < weights.count; vertex++) {
        let sum = 0;
        let largest = 0;
        for (let component = 0; component < 4; component++) {
            const offset = vertex * 4 + component;
            quantizedWeights[offset] = Math.round(weights.getComponent(vertex, component) * 255);
            quantizedIndices[offset] = indices.getComponent(vertex, component);
            sum += quantizedWeights[offset];
            if (quantizedWeights[offset] > quantizedWeights[vertex * 4 + largest]) {
                largest = component;
            }
        }
        // The weights of a vertex must still add up to one after rounding.
        quantizedWeights[vertex * 4 + largest] += 255 - sum;
    }
    geometry.setAttribute('skinWeight', new THREE.BufferAttribute(quantizedWeights, 4, true));
    geometry.setAttribute('skinIndex', new THREE.BufferAttribute(quantizedIndices, 4));
}

// Drops the tracks that hold a bone at its rest transform in every clip. A bone that moves in any clip keeps its tracks
// in all of them, so switching clips never leaves it in the pose of the previous clip.
function dropRestTracks(object, clips) {
    const isRest = track => {
        const { nodeName, propertyName } = THREE.PropertyBinding.parseTrackName(track.name);
        const rest = THREE.PropertyBinding.findNode(object, nodeName)[propertyName].toArray();
        const size = rest.length;
        for (let key = 0; key < track.times.length; key++) {
            const value = track.values.subarray(key * size, (key + 1) * size);
            if (propertyName === 'quaternion') {
                const dot = value.reduce((total, component, i) => total + component * rest[i], 0);
                if (Math.abs(Math.abs(dot) - 1) > 1e-6) {
                    return false;
                }
            } else if (value.some((component, i) => Math.abs(component - rest[i]) > 1e-4 * Math.max(1, Math.abs(rest[i])))) {
                return false;
            }
        }
        return true;
    };
    const moving = new Set(clips.flatMap(clip => clip.tracks.filter(track => !isRest(track)).map(track => track.name)));
    for (const clip of clips) {
        clip.tracks = clip.tracks.filter(track => moving.has(track.name));
    }
}

function renderPreview(object, idle) {
    const renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true, preserveDrawingBuffer: true });
    renderer.setPixelRatio(1);
    renderer.setSize(${PREVIEW_SIZE}, ${PREVIEW_SIZE});
    renderer.setClearColor(0x000000, 0);

    const scene = new THREE.Scene();
    scene.add(new THREE.HemisphereLight(0xffffff, 0x8d8577, 2));
    const sun = new THREE.DirectionalLight(0xffffff, 1.5);
    sun.position.set(1, 2, 3);
    scene.add(sun);
    scene.add(object);

    const mixer = new THREE.AnimationMixer(object);
    mixer.clipAction(idle).play();
    mixer.update(0);
    object.updateMatrixWorld(true);

    const box = new THREE.Box3().setFromObject(object);
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());
    // The source is in centimeters; the clip planes follow the size.
    const distance = (size.y * 1.08 / 2) / Math.tan(THREE.MathUtils.degToRad(10));
    const camera = new THREE.PerspectiveCamera(20, 1, distance / 100, distance * 4);
    camera.position.set(center.x, center.y, box.max.z + distance);
    camera.lookAt(center);
    renderer.render(scene, camera);
    const png = renderer.domElement.toDataURL('image/png').split(',')[1];
    mixer.stopAllAction();
    scene.remove(object);
    renderer.dispose();
    return png;
}

window.convert = async base64 => {
    const bytes = Uint8Array.from(atob(base64), character => character.charCodeAt(0));
    const object = new FBXLoader().parse(bytes.buffer, '');

    const animations = CLIPS.map(name => {
        const clip = object.animations.find(animation => animation.name.endsWith('_' + name));
        if (!clip) {
            throw new Error('Missing clip ' + name + ' in ' + object.animations.map(animation => animation.name).join(', '));
        }
        clip.name = name;
        // Drops the keys that repeat their neighbors; most bones hold still in most clips.
        return clip.optimize();
    });
    dropRestTracks(object, animations);

    object.traverse(node => {
        if (node.isMesh) {
            node.material = Array.isArray(node.material) ? node.material.map(toMatte) : toMatte(node.material);
            node.geometry = compact(node.geometry);
            quantizeSkin(node.geometry);
        }
    });

    // The preview poses the bones, so it runs after the export has written their rest transforms.
    const glb = await new GLTFExporter().parseAsync(object, { binary: true, animations });
    const png = renderPreview(object, animations[0]);
    return { glb: toBase64(new Uint8Array(glb)), png };
};
window.ready = true;
</script>`;

(async () => {
    fs.mkdirSync(outFolder, { recursive: true });
    const browser = await chromium.launch();
    try {
        const tab = await browser.newPage();
        tab.on('pageerror', error => console.error('page error:', error.message));
        await tab.setContent(page);
        await tab.waitForFunction(() => window.ready, null, { timeout: 60000 });
        for (const [source, name] of Object.entries(PEOPLE)) {
            const base64 = fs.readFileSync(path.join(fbxFolder, source + '.fbx')).toString('base64');
            const result = await tab.evaluate(data => window.convert(data), base64);
            const glb = Buffer.from(result.glb, 'base64');
            fs.writeFileSync(path.join(outFolder, name + '.glb'), glb);
            fs.writeFileSync(path.join(outFolder, name + '.png'), Buffer.from(result.png, 'base64'));
            console.log(`${name}.glb ${Math.round(glb.length / 1024)} KB`);
        }
    } finally {
        await browser.close();
    }
})().catch(error => {
    console.error(error);
    process.exit(1);
});
