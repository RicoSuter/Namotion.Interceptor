import {Circle, Line, Node, Rect, Txt, type NodeProps} from '@revideo/2d';
import {all, sequence, spring, type ThreadGenerator} from '@revideo/core';
import {messageCurve} from '../geometry';
import {palette, type AccentColor} from '../palette';
import {arrivalSpring, enterEasing, fonts, fontSize, moveEasing} from '../style';
import {Arrow} from './Arrow';
import {Card} from './Card';

export interface Participant {
  id: string;
  label: string;
  color?: AccentColor;
}

export interface SequenceDiagramProps extends NodeProps {
  participants: Participant[];
  width?: number;
  height?: number;
  rowHeight?: number;
}

interface Lane {
  x: number;
  header: Rect;
  highlight: Rect;
  lifeline: Line;
  color: string;
}

const headerHeight = 76;

/** Participants with lifelines; each message draws a gently curved arrow and highlights its receiver. */
export class SequenceDiagram extends Node {
  private readonly lanes = new Map<string, Lane>();
  private readonly top: number;
  private readonly rowHeight: number;
  private row = 0;

  public constructor(props: SequenceDiagramProps) {
    const {participants, width, height, rowHeight, ...rest} = props;
    super(rest);
    const totalWidth = width ?? 1500;
    const totalHeight = height ?? 760;
    this.top = -totalHeight / 2;
    this.rowHeight = rowHeight ?? 110;
    const spacing = participants.length > 1 ? totalWidth / (participants.length - 1) : 0;
    const headerWidth = Math.min(280, spacing * 0.8 || 280);

    participants.forEach((participant, index) => {
      const x = -totalWidth / 2 + index * spacing;
      const color = palette[participant.color ?? 'blue'];
      const lifeline = (
        <Line points={[[x, this.top + headerHeight / 2 + 22], [x, this.top + totalHeight]]} stroke={palette.separator}
          lineWidth={3} lineCap={'round'} lineDash={[1, 12]} end={0} />
      ) as Line;
      const highlight = (<Rect width={headerWidth} height={headerHeight} radius={20} fill={color} opacity={0} />) as Rect;
      const header = (
        <Card radius={20} x={x} y={this.top} width={headerWidth} height={headerHeight} opacity={0} scale={0.86}>
          {highlight}
          <Rect layout alignItems={'center'} gap={12}>
            <Circle size={12} fill={color} />
            <Txt fontFamily={fonts.text} fontWeight={600} fontSize={fontSize.label - 2} fill={palette.text} text={participant.label} />
          </Rect>
        </Card>
      ) as Card;
      this.add(lifeline);
      this.lanes.set(participant.id, {x, header, highlight, lifeline, color});
    });
    for (const lane of this.lanes.values()) {
      this.add(lane.header);
    }
  }

  /** Headers spring in one after another while the lifelines grow down. */
  public *appear(): ThreadGenerator {
    const lanes = [...this.lanes.values()];
    yield* all(
      sequence(0.12, ...lanes.map(lane => all(
        lane.header.opacity(1, 0.35, enterEasing),
        spring(arrivalSpring, 0.86, 1, value => lane.header.scale(value)),
      ))),
      sequence(0.12, ...lanes.map(lane => lane.lifeline.end(1, 1.1, moveEasing))),
    );
  }

  /** Draws the next message below the previous one and makes the receiver the active participant. */
  public *message(from: string, to: string, label: string, options: {reply?: boolean} = {}): ThreadGenerator {
    const source = this.lane(from);
    const target = this.lane(to);
    const y = this.top + headerHeight / 2 + 70 + this.row * this.rowHeight;
    this.row++;
    const gap = from === to ? 0 : Math.sign(target.x - source.x) * 14;
    const curve = messageCurve(source.x + gap, target.x - gap, y);
    const arrow = new Arrow({curve, color: options.reply ? palette.secondaryText : target.color, dashed: options.reply});
    const labelX = from === to ? source.x + 130 : (source.x + target.x) / 2;
    const text = (
      <Txt x={labelX} y={from === to ? y : y - 52} offset={from === to ? [-1, 0] : [0, 0]} opacity={0}
        fontFamily={fonts.text} fontWeight={500} fontSize={fontSize.detail + 2} fill={palette.text} text={label} />
    ) as Txt;
    this.add(arrow);
    this.add(text);
    yield* all(arrow.grow(0.8), text.opacity(1, 0.5, enterEasing), this.activate(to));
  }

  /** Highlights one participant; the others return to normal. */
  public *activate(id: string): ThreadGenerator {
    yield* all(...[...this.lanes.entries()].map(([laneId, lane]) => lane.highlight.opacity(laneId === id ? 0.28 : 0, 0.4)));
  }

  private lane(id: string): Lane {
    const lane = this.lanes.get(id);
    if (!lane) {
      throw new Error(`Sequence diagram has no participant '${id}'`);
    }
    return lane;
  }
}
