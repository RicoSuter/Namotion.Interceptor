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
}

/** macOS browser window around a recorded demo clip. */
export class BrowserFrame extends WindowFrame {
  private readonly video = createRef<ClipVideo>();
  private readonly clipDuration: number;
  private readonly clipMarks: Record<string, number>;
  private readonly demo: string;

  public constructor(props: BrowserFrameProps) {
    const {demo, address, width, aspectRatio, ...rest} = props;
    const videoHeight = width / (aspectRatio ?? 1.6);
    super({...rest, width, height: videoHeight + titleBarHeight, address, bodyFill: palette.background});
    const clip = useClip(demo);
    this.clipDuration = clip.duration;
    this.clipMarks = clip.marks;
    this.demo = demo;
    this.body.add(<ClipVideo ref={this.video} src={clip.url} width={width} height={videoHeight} />);
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
