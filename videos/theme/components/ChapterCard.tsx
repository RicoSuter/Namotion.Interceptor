import {Circle, Gradient, Node, Rect, Txt, blur, type NodeProps} from '@revideo/2d';
import {all, createSignal, sequence, useThread, type ThreadGenerator} from '@revideo/core';
import {palette, type AccentColor} from '../palette';
import {enterEasing, fonts, fontSize, moveEasing} from '../style';

export interface ChapterCardProps extends NodeProps {
  title: string;
  /** Small line above the title, for example the chapter number. */
  kicker?: string;
  /** Colors of the background wash. */
  colors?: [AccentColor, AccentColor, AccentColor];
}

const titleLetterSpacing = -3;

/** Full-screen chapter title over a slowly drifting gradient wash; letters rise in with a stagger. */
export class ChapterCard extends Node {
  private readonly letters: Txt[] = [];
  private readonly kickerText: Txt | null = null;
  private readonly wash = createSignal(0);
  private readonly content: Node;

  public constructor(props: ChapterCardProps) {
    const {title, kicker, colors, ...rest} = props;
    super(rest);
    const time = useThread().time;
    const [first, second, third] = (colors ?? ['blue', 'purple', 'cyan']).map(color => palette[color]);
    const blobs = [
      {color: first, x: -520, y: -260, size: 1500, speed: 0.11, phase: 0},
      {color: second, x: 560, y: 220, size: 1600, speed: 0.09, phase: 2},
      {color: third, x: 140, y: -420, size: 1100, speed: 0.13, phase: 4},
    ];

    this.add(<Rect width={1920} height={1080} fill={palette.background} />);
    for (const blob of blobs) {
      this.add(
        <Circle size={blob.size} opacity={() => this.wash() * 0.3}
          x={() => blob.x + Math.sin(time() * blob.speed * Math.PI * 2 + blob.phase) * 140}
          y={() => blob.y + Math.cos(time() * blob.speed * Math.PI * 2 + blob.phase) * 90}
          fill={new Gradient({type: 'radial', fromRadius: 0, toRadius: blob.size / 2,
            stops: [{offset: 0, color: blob.color}, {offset: 1, color: `${blob.color}00`}]})} />,
      );
    }

    this.content = new Node({});
    this.add(this.content);
    const widths = measureWidths(title, `700 ${fontSize.title}px ${fonts.text}`, titleLetterSpacing);
    const left = -widths[widths.length - 1] / 2;
    const titleY = kicker ? 30 : 0;
    [...title].forEach((letter, index) => {
      const node = (
        <Txt text={letter} offset={[-1, 0]} x={left + widths[index]} y={titleY + 60} opacity={0}
          fontFamily={fonts.text} fontWeight={700} fontSize={fontSize.title} letterSpacing={titleLetterSpacing}
          fill={palette.text} filters={[blur(10)]} />
      ) as Txt;
      this.letters.push(node);
      this.content.add(node);
    });
    if (kicker) {
      this.kickerText = (
        <Txt text={kicker.toUpperCase()} y={-80} opacity={0} fontFamily={fonts.text} fontWeight={600}
          fontSize={fontSize.kicker} letterSpacing={6} fill={palette.secondaryText} />
      ) as Txt;
      this.content.add(this.kickerText);
    }
  }

  public *enter(): ThreadGenerator {
    const titleY = this.kickerText ? 30 : 0;
    yield* all(
      this.wash(1, 1.4, moveEasing),
      this.kickerText ? this.kickerText.opacity(1, 0.8, enterEasing) : nothing(),
      sequence(0.035, ...this.letters.map(letter => all(
        letter.opacity(1, 0.7, enterEasing),
        letter.y(titleY, 0.8, enterEasing),
        letter.filters.blur(0, 0.7, enterEasing),
      ))),
    );
  }

  public *exit(): ThreadGenerator {
    yield* all(
      this.content.opacity(0, 0.5, moveEasing),
      this.content.y(-40, 0.6, moveEasing),
      this.opacity(0, 0.7, moveEasing),
    );
    this.remove();
  }
}

function* nothing(): ThreadGenerator {}

/** Left edge of every letter and the full width as the last entry, as the canvas would lay them out. */
function measureWidths(text: string, font: string, letterSpacing: number): number[] {
  const context = document.createElement('canvas').getContext('2d')!;
  context.font = font;
  context.letterSpacing = `${letterSpacing}px`;
  return [...Array(text.length + 1).keys()].map(length => context.measureText(text.slice(0, length)).width);
}
