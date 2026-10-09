import {Node, Rect, Txt, type NodeProps} from '@revideo/2d';
import {all, createSignal, delay, type SimpleSignal, type ThreadGenerator} from '@revideo/core';
import type {HeaderZone} from '../geometry';
import {palette} from '../palette';
import {enterEasing, fonts, fontSize, moveEasing, safeMargin} from '../style';

/** Displayed in the header only; narration never says it. */
export const productName = 'Namotion.Interceptor';

const letterSpacing = 0.3;
const separatorGap = 18;
const quietWeight = 500;
const chapterWeight = 600;
const slide = 8;
const frameLeft = -960;
const frameTop = -540;
/** Space content keeps from the header text, to its right and below it. */
const clearance = {x: 48, y: 44};

export interface ChapterHeaderProps extends NodeProps {
  /** The video's title from script.yaml, shown between the product name and the chapter title; omitted when empty. */
  videoTitle?: string;
}

/**
 * Quiet "Namotion.Interceptor | Video title | Chapter title" line in the top left corner. The narrator adds it to
 * every scene and switches the chapter title when the chapter changes; a full-screen ChapterCard covers it while
 * shown.
 */
export class ChapterHeader extends Node {
  /** Faded in by the narrator when the scene starts. */
  public readonly visibility = createSignal(0);
  /** Raised by a ChapterCard while it fills the frame. */
  public readonly coverage = createSignal(0);
  /** Left edge of the chapter title, relative to the header's left edge. */
  private readonly chapterX: number;
  private title: string | null = null;
  private titleWidth = 0;
  private current: {text: Txt; opacity: SimpleSignal<number>} | null = null;

  public constructor({videoTitle = '', ...props}: ChapterHeaderProps = {}) {
    super({zIndex: 100, ...props});
    const top = frameTop + safeMargin.y + fontSize.header / 2;
    // Children carry the opacity: a node with opacity below 1 draws through a cache that misses its children's
    // paint changes, such as the title cross fade.
    this.position(() => [frameLeft + safeMargin.x, top + slide * (1 - this.presence())]);
    let x = 0;
    for (const segment of [productName, videoTitle].filter(text => text.length > 0)) {
      this.add(
        <Txt text={segment} offset={[-1, 0]} x={x} opacity={() => this.presence()}
          fontFamily={fonts.text} fontWeight={quietWeight} fontSize={fontSize.header} letterSpacing={letterSpacing}
          fill={palette.secondaryText} />,
      );
      x += measureWidth(segment, quietWeight) + separatorGap;
      this.add(
        <Rect x={x} width={1.5} height={fontSize.header * 0.9} fill={palette.separator} opacity={() => this.presence()} />,
      );
      x += separatorGap;
    }
    this.chapterX = x;
  }

  /** Finds the header the narrator added to the view. */
  public static of(node: Node): ChapterHeader | null {
    return node.view().findFirst<ChapterHeader>(candidate => candidate instanceof ChapterHeader);
  }

  /**
   * The header's region with its clearance, in view coordinates, sized for the chapter title shown last. Content
   * keeps out of it.
   */
  public zone(): HeaderZone {
    return {
      left: frameLeft,
      right: frameLeft + safeMargin.x + this.chapterX + this.titleWidth + clearance.x,
      bottom: frameTop + safeMargin.y + fontSize.header + clearance.y,
    };
  }

  public *appear(duration = 0.6): ThreadGenerator {
    yield* this.visibility(1, duration, enterEasing);
  }

  /** Shows the chapter title; the previous one fades out sliding up while the new one rises from below. */
  public *show(title: string): ThreadGenerator {
    if (title === this.title) {
      return;
    }
    this.title = title;
    this.titleWidth = measureWidth(title, chapterWeight);
    const opacity = createSignal(0);
    const text = (
      <Txt text={title} offset={[-1, 0]} x={this.chapterX} opacity={() => this.presence() * opacity()}
        fontFamily={fonts.text} fontWeight={chapterWeight} fontSize={fontSize.header} letterSpacing={letterSpacing}
        fill={palette.text} />
    ) as Txt;
    this.add(text);
    const previous = this.current;
    this.current = {text, opacity};
    if (!previous) {
      opacity(1);
      return;
    }
    text.y(slide);
    yield* all(
      previous.opacity(0, 0.3, moveEasing),
      previous.text.y(-slide, 0.4, moveEasing),
      delay(0.25, all(opacity(1, 0.5, enterEasing), text.y(0, 0.6, enterEasing))),
    );
    previous.text.remove();
  }

  private presence(): number {
    return this.visibility() * (1 - this.coverage());
  }
}

function measureWidth(text: string, weight: number): number {
  const context = document.createElement('canvas').getContext('2d')!;
  context.font = `${weight} ${fontSize.header}px ${fonts.text}`;
  context.letterSpacing = `${letterSpacing}px`;
  return context.measureText(text).width;
}
