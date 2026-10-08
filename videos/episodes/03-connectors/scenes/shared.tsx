import {Circle, Node, Rect, Txt, type NodeProps} from '@revideo/2d';
import {all, createSignal, delay, spring, transformVectorAsPoint, Vector2, type ThreadGenerator} from '@revideo/core';
import type {Arrow} from '../../../theme/components/Arrow';
import {BrowserFrame} from '../../../theme/components/BrowserFrame';
import {Card} from '../../../theme/components/Card';
import type {ClipRange} from '../../../theme/clips';
import {palette, type AccentColor} from '../../../theme/palette';
import {arrivalSpring, enterEasing, fonts, fontSize, moveEasing, smallShadow} from '../../../theme/style';

/** Size of one status page in CSS pixels; the split recording holds two of them side by side. */
const pageWidth = 800;
const pageHeight = 720;
const windowWidth = 820;
const titleBarHeight = 56;

/** Server and client status pages in two browser windows, both cropped from the same split recording. */
export class MachineWindows extends Node {
  public readonly server: BrowserFrame;
  public readonly client: BrowserFrame;

  public constructor(demo: string, props: NodeProps = {}) {
    super(props);
    const common = {demo, width: windowWidth, aspectRatio: (pageWidth * 2) / pageHeight, y: -70, opacity: 0, scale: 0.94};
    this.server = new BrowserFrame({...common, address: 'localhost:5310', crop: {left: 0, width: 0.5}, x: -455});
    this.client = new BrowserFrame({...common, address: 'localhost:5311', crop: {left: 0.5, width: 0.5}, x: 455});
    this.add(this.server);
    this.add(this.client);
  }

  /** Clip time of a mark the demo recorded. */
  public mark(name: string): number {
    return this.server.mark(name);
  }

  /** Both windows spring in, the server first. */
  public *arrive(): ThreadGenerator {
    yield* all(
      arrive(this.server),
      delay(0.15, arrive(this.client)),
    );
  }

  /** Plays the same range of the recording in both windows. */
  public *play(duration: number, range?: ClipRange): ThreadGenerator {
    yield* all(this.server.play(duration, range), this.client.play(duration, range));
  }

  /** World position of a point on a page, given in CSS pixels of that page. */
  public pagePoint(window: BrowserFrame, x: number, y: number): Vector2 {
    const scale = windowWidth / pageWidth;
    const local = new Vector2((x - pageWidth / 2) * scale, (y - pageHeight / 2) * scale + titleBarHeight / 2);
    return transformVectorAsPoint(local, window.localToWorld());
  }
}

/** Fades a node in while it springs to full size. */
export function* arrive(node: Node, from = 0.9): ThreadGenerator {
  yield* all(node.opacity(1, 0.45, enterEasing), spring(arrivalSpring, from, 1, value => node.scale(value)));
}

/** Fades a node out while it drifts a little. */
export function* leave(node: Node, dx = 0, dy = -40, duration = 0.6): ThreadGenerator {
  yield* all(node.opacity(0, duration, moveEasing), node.position(node.position().add([dx, dy]), duration, moveEasing));
}

/** Small rounded label with an accent dot, for values, chips and tags. */
export class Pill extends Card {
  private readonly label: Txt;

  public constructor(props: NodeProps & {text: string; color?: AccentColor; code?: boolean; size?: number; fill?: string}) {
    const {text, color, code, size, fill, ...rest} = props;
    const textSize = size ?? fontSize.label;
    super({layout: true, padding: [textSize * 0.45, textSize * 0.8], gap: 14, alignItems: 'center', radius: 999, fill: fill ?? palette.elevated, ...rest});
    if (color) {
      this.add(<Circle size={textSize * 0.42} fill={palette[color]} />);
    }
    this.label = (
      <Txt fontFamily={code ? fonts.code : fonts.text} fontWeight={code ? 400 : 600} fontSize={textSize} fill={palette.text} text={text} />
    ) as Txt;
    this.add(this.label);
  }

  /** Replaces the text with a short cross fade. */
  public *retext(text: string, duration = 0.5): ThreadGenerator {
    yield* this.label.opacity(0, duration / 2, moveEasing);
    this.label.text(text);
    yield* this.label.opacity(1, duration / 2, enterEasing);
  }
}

/** A particle that travels a straight line between two points, then fades. */
export function* travel(parent: Node, from: Vector2, to: Vector2, color: AccentColor, duration = 0.9): ThreadGenerator {
  const progress = createSignal(0);
  const particle = (
    <Circle size={18} fill={palette[color]} opacity={0} {...smallShadow}
      position={() => Vector2.lerp(from, to, progress())} />
  ) as Circle;
  parent.add(particle);
  yield* all(particle.opacity(1, 0.15), progress(1, duration, moveEasing));
  yield* particle.opacity(0, 0.2);
  particle.remove();
}

/**
 * Moves a node along an arrow's curve from one fraction of its length to another. The node and the arrow must
 * share a parent; the node keeps its final position afterwards.
 */
export function* ride(node: Node, arrow: Arrow, from: number, to: number, duration: number): ThreadGenerator {
  const progress = createSignal(from);
  node.position(() => arrow.line.getPointAtPercentage(progress()).position.add(arrow.position()));
  yield* progress(to, duration, moveEasing);
  node.position(node.position());
}

/** Converts a world point into the local space of a node, for placing things next to diagram nodes. */
export function toLocal(parent: Node, world: Vector2): Vector2 {
  return transformVectorAsPoint(world, parent.worldToLocal());
}

/** A soft rounded highlight band, for example behind a range of a sequence diagram. */
export function band(props: NodeProps & {width: number; height: number; color: AccentColor}): Rect {
  const {width, height, color, ...rest} = props;
  return (<Rect width={width} height={height} radius={24} fill={palette[color]} opacity={0} {...rest} />) as Rect;
}

/** A card with a label and a large value, for showing one property on each side of a connection. */
export class ValueCard extends Card {
  private readonly valueText: Txt;
  public readonly glow: Rect;

  public constructor(props: NodeProps & {label: string; value: string; color: AccentColor; width?: number; height?: number; valueSize?: number}) {
    const {label, value, color, width, height, valueSize, ...rest} = props;
    super({width: width ?? 440, height: height ?? 240, ...rest});
    this.glow = (<Rect width={width ?? 440} height={height ?? 240} radius={24} fill={palette.green} opacity={0} />) as Rect;
    this.add(this.glow);
    this.valueText = (
      <Txt fontFamily={fonts.text} fontWeight={700} fontSize={valueSize ?? 84} fill={palette.text} text={value} y={22} />
    ) as Txt;
    this.add(
      <Rect layout y={-62} gap={12} alignItems={'center'}>
        <Circle size={14} fill={palette[color]} />
        <Txt fontFamily={fonts.text} fontWeight={600} fontSize={fontSize.label} fill={palette.secondaryText} text={label} />
      </Rect>,
    );
    this.add(this.valueText);
  }

  /** Replaces the value with a short cross fade and a nudge. */
  public *setValue(value: string, duration = 0.5): ThreadGenerator {
    yield* this.valueText.opacity(0, duration / 2, moveEasing);
    this.valueText.text(value);
    yield* all(this.valueText.opacity(1, duration / 2, enterEasing), this.scale(1.04, 0.15).to(1, 0.35));
  }
}
