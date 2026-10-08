import {Node, Rect, Txt, type RectProps} from '@revideo/2d';
import {all, createRef, createSignal, linear, sequence, tween, useThread, waitFor, type ThreadGenerator} from '@revideo/core';
import {palette} from '../palette';
import {durations, enterEasing, fonts, fontSize, moveEasing} from '../style';
import {parseTranscript, wrapLines} from '../terminalTranscript';
import {titleBarHeight, WindowFrame} from './WindowFrame';

export interface TerminalProps extends Omit<RectProps, 'height'> {
  /** Captured transcript: the prompt line followed by the output. */
  transcript: string;
  width: number;
  /** Rows of output kept on screen; older rows scroll up. The window fits its content up to this. */
  maximumRows?: number;
  title?: string;
}

const padding = 36;
const lineHeight = 46;
/** Advance of one JetBrains Mono character relative to the font size. */
const characterWidth = 0.6;

/** macOS terminal window that types the captured command and streams its output. */
export class Terminal extends WindowFrame {
  private readonly scroll = createSignal(0);
  private readonly outputLines: Txt[] = [];
  private readonly commandText = createRef<Txt>();
  private readonly cursor = createRef<Rect>();
  private readonly command: string;
  private readonly visibleRows: number;

  public constructor(props: TerminalProps) {
    const {transcript, title, maximumRows, ...rest} = props;
    const parsed = parseTranscript(transcript);
    const columns = Math.floor((props.width - padding * 2) / (fontSize.terminal * characterWidth));
    const output = wrapLines(parsed.output, columns);
    const visibleRows = Math.min(output.length + 1, maximumRows ?? 12);
    super({...rest, height: titleBarHeight + padding * 2 + visibleRows * lineHeight, title: title ?? 'Terminal', bodyFill: '#161618'});
    this.command = parsed.command;
    this.visibleRows = visibleRows;

    const left = -this.bodyWidth / 2 + padding;
    const top = -this.bodyHeight / 2 + padding;
    const text = {fontFamily: fonts.code, fontSize: fontSize.terminal, lineHeight};
    const time = useThread().time;
    const content = new Node({y: () => -this.scroll()});
    this.body.add(<Rect width={this.bodyWidth} height={this.bodyHeight} clip>{content}</Rect>);
    content.add(
      <Rect layout offset={[-1, -1]} x={left} y={top} gap={14} alignItems={'center'}>
        <Txt {...text} fill={palette.green} text={'$'} />
        <Txt ref={this.commandText} {...text} fill={palette.text} text={''} />
        <Rect ref={this.cursor} width={14} height={30} radius={3} fill={palette.secondaryText}
          opacity={() => (Math.floor(time() * 2) % 2 === 0 ? 0.9 : 0)} />
      </Rect>,
    );
    output.forEach((line, index) => {
      const node = (
        <Txt {...text} offset={[-1, -1]} x={left} y={top + (index + 1) * lineHeight} fill={palette.secondaryText}
          text={line === '' ? ' ' : line} opacity={0} />
      ) as Txt;
      this.outputLines.push(node);
      content.add(node);
    });
  }

  /** Types the command, then streams the output so the last line arrives within the duration. */
  public *run(duration: number): ThreadGenerator {
    const typing = Math.min(1.2, duration * 0.3);
    // Set imperatively: a reactive text would create its text nodes outside the scene while drawing.
    yield* tween(typing, value => this.commandText().text(this.command.slice(0, Math.round(linear(value) * this.command.length))));
    this.cursor().remove();
    const step = Math.min(0.12, Math.max(duration - typing - 0.4, 0.2) / Math.max(this.outputLines.length, 1));
    yield* all(
      sequence(step, ...this.outputLines.map((line, index) => {
        const overflow = index + 2 - this.visibleRows;
        return all(
          line.opacity(1, durations.fast, enterEasing),
          overflow > 0 ? this.scroll(overflow * lineHeight, step * 0.9, moveEasing) : waitFor(0),
        );
      })),
      waitFor(duration - typing),
    );
  }
}
