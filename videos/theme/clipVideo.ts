import {Video} from '@revideo/2d';

/** Video that can be positioned before it starts playing. */
export class ClipVideo extends Video {
  public seekTo(seconds: number): void {
    this.time(seconds);
  }
}
