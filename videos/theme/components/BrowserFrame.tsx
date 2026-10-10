import type {RectProps} from '@revideo/2d';
import {createRef, waitFor, type ThreadGenerator} from '@revideo/core';
import {fitClip, type ClipRange} from '../clips';
import {ClipVideo} from '../clipVideo';
import {palette} from '../palette';
import {useClip} from '../variables';
import {titleBarHeight, WindowFrame} from './WindowFrame';

export interface BrowserFrameProps extends Omit<RectProps, 'width' | 'height'> {
  /** Name of the recorded demo clip. */
  demo: string;
  /** Text in the address field. */
  address: string;
  width: number;
  /** Width over height of the recording. Defaults to the 1280 by 800 capture viewport. */
  aspectRatio?: number;
  /**
   * Horizontal part of the recording to show, as fractions of its width, for example `{left: 0.5, width: 0.5}`
   * for the right half. Lets two windows show the two halves of one split recording, which stay in sync.
   */
  crop?: {left: number; width: number};
}

/** macOS browser window around a recorded demo clip. */
export class BrowserFrame extends WindowFrame {
  private readonly video = createRef<ClipVideo>();
  private readonly clipDuration: number;
  private readonly clipMarks: Record<string, number>;
  private readonly demo: string;

  public constructor(props: BrowserFrameProps) {
    const {demo, address, width, aspectRatio, crop, ...rest} = props;
    const {left, width: share} = crop ?? {left: 0, width: 1};
    const videoWidth = width / share;
    const videoHeight = videoWidth / (aspectRatio ?? 1.6);
    super({...rest, width, height: videoHeight + titleBarHeight, address, bodyFill: palette.background});
    const clip = useClip(demo);
    this.clipDuration = clip.duration;
    this.clipMarks = clip.marks;
    this.demo = demo;
    // The body clips to the window, so shifting the video shows only the cropped part.
    const x = videoWidth * (0.5 - left - share / 2);
    this.body.add(<ClipVideo ref={this.video} src={clip.url} x={x} width={videoWidth} height={videoHeight} />);
  }

  /** Clip time in seconds of a moment the demo recorded with mark, for example as a play range. */
  public mark(name: string): number {
    const time = this.clipMarks[name];
    if (time === undefined) {
      throw new Error(`Clip '${this.demo}' has no mark '${name}'. Run 'npm run capture -- <episode> --only ${this.demo}'.`);
    }
    return time;
  }

  /**
   * Plays the clip so that it ends exactly after the given duration, speeding it up or trimming its start
   * as needed (see fitClip).
   */
  public *play(duration: number, range?: ClipRange): ThreadGenerator {
    const fit = fitClip(this.clipDuration, duration, range);
    const video = this.video();
    yield video;
    video.playbackRate(fit.rate);
    video.seekTo(fit.start);
    video.play();
    yield* waitFor(duration);
    video.pause();
  }
}
