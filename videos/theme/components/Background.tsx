import {Node, type NodeProps} from '@revideo/2d';
import {createSignal, transformVectorAsPoint, useThread, Vector2, type SimpleSignal, type ThreadGenerator} from '@revideo/core';
import type {BackgroundVariant} from '../backgrounds';
import {palette, type AccentColor} from '../palette';
import {moveEasing} from '../style';
import {Camera} from './Camera';

type Rgb = [number, number, number];

const width = 1920;
const height = 1080;
/** Seconds for one full loop of the drifting glows. */
const driftPeriod = 60;
const chapterTintDuration = 2;

/** Hue pairs of the chapter-tint variant, by chapter index, repeating. */
// Cool hues only: warm accents and green turn brown or olive at this low intensity over the dark base.
const chapterHues: Array<[AccentColor, AccentColor]> = [
  ['blue', 'purple'],
  ['cyan', 'blue'],
  ['purple', 'blue'],
  ['cyan', 'purple'],
  ['blue', 'cyan'],
  ['purple', 'cyan'],
];

interface Blob {
  x: number;
  y: number;
  radius: number;
  /** Drift amplitude in pixels. */
  ax: number;
  ay: number;
  phase: number;
  intensity: number;
}

const blobs: Blob[] = [
  {x: -620, y: -300, radius: 1000, ax: 220, ay: 120, phase: 0, intensity: 0.11},
  {x: 680, y: 340, radius: 1050, ax: 200, ay: 140, phase: 2.1, intensity: 0.1},
  {x: 620, y: -420, radius: 800, ax: 170, ay: 100, phase: 4.2, intensity: 0.075},
  {x: -560, y: 460, radius: 850, ax: 160, ay: 110, phase: 1.1, intensity: 0.07},
];

export interface BackgroundProps extends NodeProps {
  variant: BackgroundVariant;
}

/**
 * Full-frame layer behind all content: the base color, slow colored glows chosen by the variant, and a fixed
 * grain that dithers the gradients. The narrator adds it to the view, outside the camera, so it stays fixed while
 * the camera moves.
 */
export class Background extends Node {
  /** Episode seconds at scene time zero, so the drift continues across scene cuts. */
  public readonly timeOffset = createSignal(0);
  private readonly variant: BackgroundVariant;
  private readonly sceneTime: SimpleSignal<number>;
  private readonly tint = createSignal(1);
  private hues: {from: [Rgb, Rgb]; to: [Rgb, Rgb]} | null = null;
  private chapterIndex = -1;
  private camera: Camera | null = null;

  public constructor(props: BackgroundProps) {
    const {variant, ...rest} = props;
    super({zIndex: -100, ...rest});
    this.variant = variant;
    this.sceneTime = useThread().time;
  }

  /** Moves the chapter-tint variant to the hues of a chapter; the first call sets them without a fade. */
  public *showChapter(index: number): ThreadGenerator {
    if (index === this.chapterIndex) {
      return;
    }
    const [first, second] = chapterHues[index % chapterHues.length];
    const target: [Rgb, Rgb] = [rgb(palette[first]), rgb(palette[second])];
    this.chapterIndex = index;
    if (!this.hues) {
      this.hues = {from: target, to: target};
      return;
    }
    this.hues = {from: this.currentHues(), to: target};
    this.tint(0);
    yield* this.tint(1, chapterTintDuration, moveEasing);
  }

  protected override async draw(context: CanvasRenderingContext2D): Promise<void> {
    context.save();
    context.fillStyle = palette.background;
    context.fillRect(-width / 2, -height / 2, width, height);
    context.globalCompositeOperation = 'lighter';
    const time = this.timeOffset() + this.sceneTime();
    switch (this.variant) {
      case 'drift':
        this.drawBlobs(context, time, [rgb(palette.blue), rgb(palette.purple), rgb(palette.cyan), mix(rgb(palette.blue), rgb(palette.purple), 0.5)]);
        break;
      case 'chapter-tint': {
        const [first, second] = this.currentHues();
        this.drawBlobs(context, time, [first, second, second, first]);
        break;
      }
      case 'edge-aurora':
        drawAurora(context, time);
        break;
      case 'follow-light':
        this.drawLight(context, time);
        break;
    }
    context.restore();
    drawGrain(context);
    await this.drawChildren(context);
  }

  private drawBlobs(context: CanvasRenderingContext2D, time: number, colors: Rgb[]): void {
    const angle = (time / driftPeriod) * Math.PI * 2;
    blobs.forEach((blob, index) => {
      glow(context, blob.x + Math.sin(angle + blob.phase) * blob.ax, blob.y + Math.cos(angle + blob.phase * 1.3) * blob.ay,
        blob.radius, blob.radius, colors[index], blob.intensity);
    });
  }

  private drawLight(context: CanvasRenderingContext2D, time: number): void {
    this.camera ??= this.view().findFirst<Camera>(node => node instanceof Camera);
    let center = Vector2.zero;
    if (this.camera) {
      // The camera and this layer share the view as parent, so the camera's parent space is this layer's space.
      center = transformVectorAsPoint(this.camera.focus(), this.camera.localToParent());
    }
    const angle = (time / driftPeriod) * Math.PI * 2;
    const wobble = new Vector2(Math.sin(angle) * 40, Math.cos(angle * 1.3) * 30);
    glow(context, center.x + wobble.x, center.y + wobble.y, 1050, 900, mix(rgb(palette.blue), rgb(palette.cyan), 0.35), 0.12);
    glow(context, center.x + 260 + wobble.y, center.y + 160 - wobble.x, 700, 600, rgb(palette.purple), 0.045);
  }

  private currentHues(): [Rgb, Rgb] {
    const hues = this.hues ?? {from: [rgb(palette.blue), rgb(palette.purple)], to: [rgb(palette.blue), rgb(palette.purple)]};
    const progress = this.tint();
    return [mix(hues.from[0], hues.to[0], progress), mix(hues.from[1], hues.to[1], progress)];
  }
}

/** Soft colored glows along the top and bottom edges that slowly breathe; the center stays neutral. */
function drawAurora(context: CanvasRenderingContext2D, time: number): void {
  const angle = (time / driftPeriod) * Math.PI * 2;
  const breath = (phase: number) => 0.75 + 0.25 * Math.sin((time / 14) * Math.PI * 2 + phase);
  const lights: Array<{x: number; y: number; rx: number; ry: number; color: AccentColor; intensity: number; phase: number}> = [
    {x: -640, y: -560, rx: 950, ry: 400, color: 'blue', intensity: 0.22, phase: 0},
    {x: 100, y: -580, rx: 850, ry: 360, color: 'purple', intensity: 0.17, phase: 2},
    {x: 780, y: -560, rx: 800, ry: 380, color: 'cyan', intensity: 0.15, phase: 4},
    {x: -420, y: 580, rx: 950, ry: 360, color: 'cyan', intensity: 0.14, phase: 1},
    {x: 560, y: 590, rx: 950, ry: 380, color: 'blue', intensity: 0.18, phase: 3},
  ];
  for (const light of lights) {
    glow(context, light.x + Math.sin(angle + light.phase) * 140, light.y, light.rx, light.ry, rgb(palette[light.color]),
      light.intensity * breath(light.phase));
  }
}

/** Additive radial glow with a gaussian falloff; many stops keep the falloff smooth. */
function glow(context: CanvasRenderingContext2D, x: number, y: number, radiusX: number, radiusY: number, color: Rgb, intensity: number): void {
  context.save();
  context.translate(x, y);
  context.scale(1, radiusY / radiusX);
  const gradient = context.createRadialGradient(0, 0, 0, 0, 0, radiusX);
  const stops = 12;
  const floor = Math.exp(-4.5);
  for (let index = 0; index <= stops; index++) {
    const t = index / stops;
    const falloff = (Math.exp(-4.5 * t * t) - floor) / (1 - floor);
    gradient.addColorStop(t, `rgba(${color[0]}, ${color[1]}, ${color[2]}, ${(intensity * falloff).toFixed(4)})`);
  }
  context.fillStyle = gradient;
  context.fillRect(-radiusX, -radiusX, radiusX * 2, radiusX * 2);
  context.restore();
}

/** A fixed grain over the whole frame, for example behind content or over a chapter card's wash. */
export class Grain extends Node {
  public constructor(props: NodeProps = {}) {
    super(props);
  }

  protected override async draw(context: CanvasRenderingContext2D): Promise<void> {
    drawGrain(context);
    await this.drawChildren(context);
  }
}

const grainSize = 256;
/** Largest deviation of the grain from neutral gray, in 8-bit levels before the overlay blend. */
const grainAmplitude = 12;
let grainTile: HTMLCanvasElement | undefined;

/**
 * Overlays fixed noise on what is drawn so far. Gradients on the dark base step one 8-bit level every few dozen
 * pixels, which shows as rings; the noise breaks the steps up.
 */
function drawGrain(context: CanvasRenderingContext2D): void {
  grainTile ??= createGrainTile();
  context.save();
  context.globalCompositeOperation = 'overlay';
  context.fillStyle = context.createPattern(grainTile, 'repeat')!;
  context.fillRect(-width / 2, -height / 2, width, height);
  context.restore();
}

function createGrainTile(): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  canvas.width = grainSize;
  canvas.height = grainSize;
  const context = canvas.getContext('2d')!;
  const image = context.createImageData(grainSize, grainSize);
  // Seeded, so every frame and every render draws the same grain.
  let seed = 0x2f6b9a1d;
  const random = () => {
    seed = (seed + 0x6d2b79f5) | 0;
    let value = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    value = (value + Math.imul(value ^ (value >>> 7), 61 | value)) ^ value;
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296;
  };
  for (let index = 0; index < image.data.length; index += 4) {
    // Triangular distribution, the usual dither shape.
    const level = 128 + Math.round((random() + random() - 1) * grainAmplitude);
    image.data[index] = level;
    image.data[index + 1] = level;
    image.data[index + 2] = level;
    image.data[index + 3] = 255;
  }
  context.putImageData(image, 0, 0);
  return canvas;
}

function rgb(hex: string): Rgb {
  const value = Number.parseInt(hex.slice(1), 16);
  return [(value >> 16) & 255, (value >> 8) & 255, value & 255];
}

function mix(from: Rgb, to: Rgb, progress: number): Rgb {
  return [0, 1, 2].map(index => Math.round(from[index] + (to[index] - from[index]) * progress)) as Rgb;
}
