import {Rect, Txt, type RectProps} from '@revideo/2d';
import {all, createRef, type ThreadGenerator} from '@revideo/core';
import {palette} from '../palette';
import {enterEasing, fonts, fontSize, radius, smallShadow} from '../style';

/** Subtitle pill anchored to the bottom of the frame; fades between lines. */
export class Caption extends Rect {
  private readonly label = createRef<Txt>();
  private readonly baseY: number;
  private current = '';

  public constructor(props: RectProps & {bottom?: number}) {
    const bottom = props.bottom ?? 492;
    super({
      layout: true,
      offset: [0, 1],
      y: bottom,
      padding: [14, 30],
      radius: radius.pill,
      fill: 'rgba(44, 44, 46, 0.94)',
      opacity: 0,
      ...smallShadow,
      ...props,
    });
    this.baseY = bottom;
    this.add(
      <Txt ref={this.label} fontFamily={fonts.text} fontWeight={500} fontSize={fontSize.caption} lineHeight={46}
        fill={palette.text} textAlign={'center'} textWrap={'balance'} maxWidth={1480} text={''} />,
    );
  }

  /** Fades the current line out and the new one in; an empty line hides the pill. */
  public *show(text: string | null): ThreadGenerator {
    const next = text ?? '';
    if (next === this.current) {
      return;
    }
    if (this.current !== '') {
      yield* this.opacity(0, 0.18);
    }
    this.current = next;
    if (next === '') {
      return;
    }
    this.label().text(next);
    this.y(this.baseY + 10);
    yield* all(this.opacity(1, 0.3, enterEasing), this.y(this.baseY, 0.4, enterEasing));
  }
}
