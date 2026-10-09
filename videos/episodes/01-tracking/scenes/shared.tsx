import {Circle, Node, Rect, Txt, type NodeProps} from '@revideo/2d';
import {all, createSignal, delay, spring, transformVectorAsPoint, Vector2, type ThreadGenerator} from '@revideo/core';
import type {Arrow} from '../../../theme/components/Arrow';
import {BrowserFrame} from '../../../theme/components/BrowserFrame';
import {Card} from '../../../theme/components/Card';
import type {ClipRange} from '../../../theme/clips';
import {palette, type AccentColor} from '../../../theme/palette';
import {arrivalSpring, enterEasing, fonts, fontSize, moveEasing, smallShadow} from '../../../theme/style';

/** Size of one page in CSS pixels; a split recording holds two of them side by side. */
const pageWidth = 800;
const pageHeight = 760;
const windowWidth = 820;
const titleBarHeight = 56;

/** The two halves of one split recording in two browser windows, which stay in sync. */
export class SplitWindows extends Node {
  public readonly left: BrowserFrame;
  public readonly right: BrowserFrame;

  public constructor(demo: string, addresses: [string, string], props: NodeProps = {}) {
    super(props);
    const common = {demo, width: windowWidth, aspectRatio: (pageWidth * 2) / pageHeight, opacity: 0, scale: 0.94};
    this.left = new BrowserFrame({...common, address: addresses[0], crop: {left: 0, width: 0.5}, x: -455});
    this.right = new BrowserFrame({...common, address: addresses[1], crop: {left: 0.5, width: 0.5}, x: 455});
    this.add(this.left);
    this.add(this.right);
  }

  /** Both windows, for keeping them clear of the chapter header in camera moves. */
  public get frames(): BrowserFrame[] {
    return [this.left, this.right];
  }

  /** Clip time of a mark the demo recorded. */
  public mark(name: string): number {
    return this.left.mark(name);
  }

  /** Both windows spring in, the left one first. */
  public *arrive(): ThreadGenerator {
    yield* all(arrive(this.left, 0.94), delay(0.15, arrive(this.right, 0.94)));
  }

  /** Plays the same range of the recording in both windows. */
  public *play(duration: number, range?: ClipRange): ThreadGenerator {
    yield* all(this.left.play(duration, range), this.right.play(duration, range));
  }

  /** World position of a point on a page, given in CSS pixels of that page. */
  public pagePoint(window: BrowserFrame, x: number, y: number): Vector2 {
    const scale = windowWidth / pageWidth;
    const local = new Vector2((x - pageWidth / 2) * scale, (y - pageHeight / 2) * scale + titleBarHeight / 2);
    return transformVectorAsPoint(local, window.localToWorld());
  }
}

/** The machine page and the change stream page of the plain machine, side by side. */
export function machineAndStream(demo: string): SplitWindows {
  return new SplitWindows(demo, ['localhost:5320/brew', 'localhost:5320/brew/changes']);
}

/** The machine pages of both machines, Brew on the left and BrewAsync on the right. */
export function bothMachines(demo: string): SplitWindows {
  return new SplitWindows(demo, ['localhost:5320/brew', 'localhost:5320/brew-async']);
}

/** Fades a node in while it springs to full size. */
export function* arrive(node: Node, from = 0.9): ThreadGenerator {
  yield* all(node.opacity(1, 0.45, enterEasing), spring(arrivalSpring, from, 1, value => node.scale(value)));
}

/** Fades a node out while it drifts a little. */
export function* leave(node: Node, dx = 0, dy = -40, duration = 0.6): ThreadGenerator {
  yield* all(node.opacity(0, duration, moveEasing), node.position(node.position().add([dx, dy]), duration, moveEasing));
}

/** A short nudge that draws the eye to a node without moving it. */
export function* nudge(node: Node, amount = 1.06): ThreadGenerator {
  yield* node.scale(amount, 0.18, enterEasing).to(1, 0.32, moveEasing);
}

/** Small rounded label with an accent dot, for values, chips and tags. */
export class Pill extends Card {
  private readonly label: Txt;
  public readonly dot: Circle | null = null;

  public constructor(props: NodeProps & {text: string; color?: AccentColor; code?: boolean; size?: number; fill?: string}) {
    const {text, color, code, size, fill, ...rest} = props;
    const textSize = size ?? fontSize.label;
    super({layout: true, padding: [textSize * 0.45, textSize * 0.8], gap: 14, alignItems: 'center', radius: 999, fill: fill ?? palette.elevated, ...rest});
    if (color) {
      this.dot = (<Circle size={textSize * 0.42} fill={palette[color]} />) as Circle;
      this.add(this.dot);
    }
    this.label = (
      <Txt fontFamily={code ? fonts.code : fonts.text} fontWeight={code ? 500 : 600} fontSize={textSize} fill={palette.text} text={text} />
    ) as Txt;
    this.add(this.label);
  }

  /** Replaces the text with a short cross fade. */
  public *retext(text: string, duration = 0.5): ThreadGenerator {
    yield* this.label.opacity(0, duration / 2, moveEasing);
    this.label.text(text);
    yield* this.label.opacity(1, duration / 2, enterEasing);
  }

  /** Changes the color of the dot. */
  public *recolor(color: AccentColor | string, duration = 0.4): ThreadGenerator {
    if (this.dot) {
      yield* this.dot.fill(color in palette ? palette[color as AccentColor] : color, duration);
    }
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

/** A card with a title and rows of field names and values, such as the fields of a property change. */
export class FieldCard extends Card {
  private readonly rows: Rect[] = [];
  private readonly values: Txt[] = [];

  public constructor(props: NodeProps & {title: string; fields: Array<[string, string]>; width?: number; color?: AccentColor}) {
    const {title, fields, width, color, ...rest} = props;
    const rowHeight = 64;
    const height = 110 + fields.length * rowHeight;
    super({width: width ?? 860, height, ...rest});
    const top = -height / 2;
    this.add(
      <Rect layout x={0} y={top + 52} width={(width ?? 860) - 80} gap={14} alignItems={'center'}>
        <Circle size={14} fill={palette[color ?? 'purple']} />
        <Txt fontFamily={fonts.code} fontWeight={600} fontSize={30} fill={palette.text} text={title} />
      </Rect>,
    );
    fields.forEach(([name, value], index) => {
      const valueText = (<Txt fontFamily={fonts.code} fontSize={28} fill={palette.text} text={value} />) as Txt;
      const row = (
        <Rect layout x={0} y={top + 118 + index * rowHeight} width={(width ?? 860) - 80} justifyContent={'space-between'}
          alignItems={'center'} opacity={0}>
          <Txt fontFamily={fonts.text} fontWeight={500} fontSize={fontSize.label - 2} fill={palette.secondaryText} text={name} />
          {valueText}
        </Rect>
      ) as Rect;
      this.rows.push(row);
      this.values.push(valueText);
      this.add(row);
    });
  }

  /** The row of a field, for highlights and camera targets. */
  public row(index: number): Rect {
    return this.rows[index];
  }

  /** Slides one field in. */
  public *reveal(index: number, duration = 0.4): ThreadGenerator {
    const row = this.rows[index];
    const y = row.y();
    row.y(y + 18);
    yield* all(row.opacity(1, duration, enterEasing), row.y(y, duration, enterEasing));
  }

  /** Brightens one value and dims the others; pass -1 to show all evenly. */
  public *highlight(index: number, duration = 0.4): ThreadGenerator {
    yield* all(...this.rows.map((row, rowIndex) => row.opacity(index < 0 || rowIndex === index ? 1 : 0.35, duration)));
  }
}

/** A labeled box with a dashed look, for example the pending writes of a transaction. */
export class Box extends Node {
  public readonly frame: Rect;
  private readonly caption: Txt;

  public constructor(props: NodeProps & {label: string; width: number; height: number; color?: string}) {
    const {label, width, height, color, ...rest} = props;
    super(rest);
    this.frame = (
      <Rect width={width} height={height} radius={28} stroke={color ?? palette.separator} lineWidth={3}
        lineDash={[14, 12]} fill={'rgba(44, 44, 46, 0.55)'} />
    ) as Rect;
    this.caption = (
      <Txt y={-height / 2 - 34} fontFamily={fonts.text} fontWeight={600} fontSize={26} fill={palette.secondaryText} text={label} />
    ) as Txt;
    this.add(this.frame);
    this.add(this.caption);
  }

  public *retext(text: string, duration = 0.5): ThreadGenerator {
    yield* this.caption.opacity(0, duration / 2, moveEasing);
    this.caption.text(text);
    yield* this.caption.opacity(1, duration / 2, enterEasing);
  }
}
