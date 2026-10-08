import {Node, Rect, Txt, type NodeProps} from '@revideo/2d';
import {all, createSignal, delay, type SimpleSignal, type ThreadGenerator} from '@revideo/core';
import {palette} from '../palette';
import {enterEasing, fonts, fontSize, moveEasing, safeMargin} from '../style';

/** Displayed in the header only; narration never says it. */
export const productName = 'Namotion.Interceptor';

const letterSpacing = 0.3;
const separatorGap = 18;
const productWeight = 500;
const titleWeight = 600;
const slide = 8;

/**
 * Quiet "Namotion.Interceptor | Chapter title" line in the top right corner. The narrator adds it to every scene
 * and switches the title when the chapter changes; a full-screen ChapterCard covers it while shown.
 */
export class ChapterHeader extends Node {
  /** Faded in by the narrator when the scene starts. */
  public readonly visibility = createSignal(0);
  /** Raised by a ChapterCard while it fills the frame. */
  public readonly coverage = createSignal(0);
  private readonly prefix: Node;
  private title: string | null = null;
  private current: {text: Txt; opacity: SimpleSignal<number>} | null = null;

  public constructor(props: NodeProps = {}) {
    super({zIndex: 100, ...props});
    const top = -540 + safeMargin.y + fontSize.header / 2;
    // Children carry the opacity: a node with opacity below 1 draws through a cache that misses its children's
    // paint changes, such as the title cross fade.
    this.position(() => [960 - safeMargin.x, top + slide * (1 - this.presence())]);
    this.prefix = new Node({});
    this.prefix.add(
      <Txt text={productName} offset={[1, 0]} x={-separatorGap} opacity={() => this.presence()}
        fontFamily={fonts.text} fontWeight={productWeight} fontSize={fontSize.header} letterSpacing={letterSpacing}
        fill={palette.secondaryText} />,
    );
    this.prefix.add(
      <Rect width={1.5} height={fontSize.header * 0.9} fill={palette.separator} opacity={() => this.presence()} />,
    );
    this.add(this.prefix);
  }

  /** Finds the header the narrator added to the view. */
  public static of(node: Node): ChapterHeader | null {
    return node.view().findFirst<ChapterHeader>(candidate => candidate instanceof ChapterHeader);
  }

  public *appear(duration = 0.6): ThreadGenerator {
    yield* this.visibility(1, duration, enterEasing);
  }

  /** Shows the title; cross fades with a short upward slide when another title is already shown. */
  public *show(title: string): ThreadGenerator {
    if (title === this.title) {
      return;
    }
    this.title = title;
    const opacity = createSignal(0);
    const text = (
      <Txt text={title} offset={[1, 0]} opacity={() => this.presence() * opacity()}
        fontFamily={fonts.text} fontWeight={titleWeight} fontSize={fontSize.header} letterSpacing={letterSpacing}
        fill={palette.text} />
    ) as Txt;
    this.add(text);
    const prefixX = -(measureWidth(title) + separatorGap);
    const previous = this.current;
    this.current = {text, opacity};
    if (!previous) {
      opacity(1);
      this.prefix.x(prefixX);
      return;
    }
    text.y(slide);
    yield* all(
      previous.opacity(0, 0.3, moveEasing),
      previous.text.y(-slide, 0.4, moveEasing),
      this.prefix.x(prefixX, 0.7, moveEasing),
      delay(0.25, all(opacity(1, 0.5, enterEasing), text.y(0, 0.6, enterEasing))),
    );
    previous.text.remove();
  }

  private presence(): number {
    return this.visibility() * (1 - this.coverage());
  }
}

function measureWidth(text: string): number {
  const context = document.createElement('canvas').getContext('2d')!;
  context.font = `${titleWeight} ${fontSize.header}px ${fonts.text}`;
  context.letterSpacing = `${letterSpacing}px`;
  return context.measureText(text).width;
}
