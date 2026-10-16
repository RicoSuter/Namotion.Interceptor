import {Node, type NodeProps} from '@revideo/2d';
import {all, delay, transformVectorAsPoint, Vector2, type ThreadGenerator} from '@revideo/core';
import type {ClipRange} from '../clips';
import {arrive} from '../motion';
import {BrowserFrame} from './BrowserFrame';
import {titleBarHeight} from './WindowFrame';

export interface SplitWindowsProps extends NodeProps {
  /** Name of a demo that records two pages side by side, see `openSplit` in `tools/capture/split.ts`. */
  demo: string;
  /** Text in the address fields of the left and right window. */
  addresses: [string, string];
  /** Size of one page in CSS pixels; the recording's viewport is twice as wide. */
  pageWidth?: number;
  pageHeight: number;
  /** Width of each window. */
  windowWidth?: number;
  /** Horizontal space between the windows. */
  gap?: number;
}

/** The two halves of one split recording in two browser windows side by side, which play in sync. */
export class SplitWindows extends Node {
  public readonly left: BrowserFrame;
  public readonly right: BrowserFrame;
  private readonly pageWidth: number;
  private readonly pageHeight: number;
  private readonly windowWidth: number;

  public constructor(props: SplitWindowsProps) {
    const {demo, addresses, pageWidth, pageHeight, windowWidth, gap, ...rest} = props;
    super(rest);
    this.pageWidth = pageWidth ?? 800;
    this.pageHeight = pageHeight;
    this.windowWidth = windowWidth ?? 820;
    const offset = (this.windowWidth + (gap ?? 90)) / 2;
    const common = {demo, width: this.windowWidth, aspectRatio: (this.pageWidth * 2) / pageHeight, opacity: 0, scale: 0.94};
    this.left = new BrowserFrame({...common, address: addresses[0], crop: {left: 0, width: 0.5}, x: -offset});
    this.right = new BrowserFrame({...common, address: addresses[1], crop: {left: 0.5, width: 0.5}, x: offset});
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

  /** World position of a point on the page in `window`, given in CSS pixels of that page. */
  public pagePoint(window: BrowserFrame, x: number, y: number): Vector2 {
    const scale = this.windowWidth / this.pageWidth;
    const local = new Vector2((x - this.pageWidth / 2) * scale, (y - this.pageHeight / 2) * scale + titleBarHeight / 2);
    return transformVectorAsPoint(local, window.localToWorld());
  }
}
