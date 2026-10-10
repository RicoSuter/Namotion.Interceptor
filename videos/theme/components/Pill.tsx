import {Circle, Txt, type NodeProps} from '@revideo/2d';
import type {ThreadGenerator} from '@revideo/core';
import {palette, type AccentColor} from '../palette';
import {enterEasing, fonts, fontSize, moveEasing} from '../style';
import {Card} from './Card';

export interface PillProps extends NodeProps {
  text: string;
  /** Color of the dot before the text; no dot when omitted. */
  color?: AccentColor;
  /** Sets the text in the code font, for identifiers and values. */
  code?: boolean;
  /** Font size of the text; the padding and dot scale with it. */
  size?: number;
  fill?: string;
}

/** Small rounded label with an optional accent dot, for key terms, values and callouts. */
export class Pill extends Card {
  private readonly label: Txt;
  private readonly dot: Circle | null = null;

  public constructor(props: PillProps) {
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

  /** Changes the color of the dot; a pill without a dot stays as it is. */
  public *recolor(color: AccentColor, duration = 0.4): ThreadGenerator {
    if (this.dot) {
      yield* this.dot.fill(palette[color], duration);
    }
  }
}
