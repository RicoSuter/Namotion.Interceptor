import {Circle, Code, Rect, Txt, lines, type DrawHooks, type RectProps} from '@revideo/2d';
import {all, createRef, createSignal, linear, map, transformVectorAsPoint, tween, Vector2, type ThreadGenerator} from '@revideo/core';
import {csharp} from '../csharp';
import {scrollToFocus} from '../geometry';
import {palette} from '../palette';
import {durations, fonts, fontSize, moveEasing, radius} from '../style';
import {Card} from './Card';

export interface CodeCardProps extends RectProps {
  fileName: string;
  width: number;
  height: number;
  code?: string;
  codeFontSize?: number;
}

const headerHeight = 76;
const padding = 40;
/** Space between the filename tab and the first line of code. */
const codeGap = 20;

/** Characters that JetBrains Mono joins into ligatures, such as => and >=. */
const ligatureCharacters = /[=<>!&|+\-*/:.?]{2}/;

/** Draws tokens without font ligatures, so operators appear exactly as typed. */
const plainTokens: DrawHooks = {
  token(context, text, position, color, selection) {
    context.fillStyle = color;
    context.globalAlpha *= map(0.25, 1, selection);
    if (!ligatureCharacters.test(text)) {
      context.fillText(text, position.x, position.y);
      return;
    }
    // Drawing character by character keeps the font from joining them; the font is monospaced.
    const advance = context.measureText(text).width / text.length;
    [...text].forEach((character, index) => context.fillText(character, position.x + index * advance, position.y));
  },
};

/** Code from a compiled sample on a card with a filename tab; types in, morphs, and focuses lines. */
export class CodeCard extends Card {
  private readonly codeNode = createRef<Code>();
  private readonly scroll = createSignal(0);
  private readonly rowHeight: number;
  private readonly viewportHeight: number;
  private readonly viewportTop: number;

  public constructor(props: CodeCardProps) {
    const {fileName, code, codeFontSize, ...rest} = props;
    super(rest);
    const size = codeFontSize ?? fontSize.code;
    this.rowHeight = Math.round(size * 1.5);
    this.viewportHeight = props.height - headerHeight - codeGap - padding;
    const top = -props.height / 2;
    const viewportTop = top + headerHeight + codeGap;
    this.viewportTop = viewportTop;

    this.add(
      <Rect layout offset={[-1, 0]} x={-props.width / 2 + padding - 12} y={top + headerHeight / 2 + 8} height={40}
        padding={[0, 16]} gap={10} alignItems={'center'} radius={radius.small} fill={palette.elevated}>
        <Circle size={10} fill={palette.purple} />
        <Txt fontFamily={fonts.text} fontWeight={500} fontSize={fontSize.detail} fill={palette.secondaryText} text={fileName} />
      </Rect>,
    );
    this.add(
      <Rect y={viewportTop + this.viewportHeight / 2} width={props.width} height={this.viewportHeight} clip>
        <Code ref={this.codeNode} highlighter={csharp} offset={[-1, -1]} x={-props.width / 2 + padding}
          y={() => -this.viewportHeight / 2 - this.scroll()} fontFamily={fonts.code} fontSize={size}
          lineHeight={this.rowHeight} fill={palette.text} drawHooks={plainTokens} code={code ?? ''} />
      </Rect>,
    );
  }

  public get code(): Code {
    return this.codeNode();
  }

  /** Types the code into an empty card at an even pace. */
  public *show(code: string, duration: number = durations.slow): ThreadGenerator {
    yield* tween(duration, value => this.codeNode().code(code.slice(0, Math.round(linear(value) * code.length))));
  }

  /** Animates the differences to the next version of the code. */
  public *morph(code: string, duration = 1): ThreadGenerator {
    yield* all(this.codeNode().code(code, duration), this.codeNode().selection(lines(0, Infinity), duration), this.scroll(0, duration, moveEasing));
  }

  /** Highlights lines (zero-based, inclusive) and dims the rest, scrolling them into view when needed. */
  public *focus(from: number, to = from, duration: number = durations.normal): ThreadGenerator {
    const lineCount = this.codeNode().parsed().split('\n').length;
    const target = scrollToFocus(this.viewportHeight, lineCount * this.rowHeight, from * this.rowHeight, (to + 1) * this.rowHeight, this.scroll());
    yield* all(this.codeNode().selection(lines(from, to), duration), this.scroll(target, duration, moveEasing));
  }

  /** World position of the middle of the given lines at the current scroll, for example as a camera target. */
  public linesCenter(from: number, to = from): Vector2 {
    const y = this.viewportTop + ((from + to + 1) / 2) * this.rowHeight - this.scroll();
    return transformVectorAsPoint(new Vector2(0, y), this.localToWorld());
  }

  /** Removes the highlight. */
  public *unfocus(duration: number = durations.normal): ThreadGenerator {
    yield* this.codeNode().selection(lines(0, Infinity), duration);
  }
}
