import {Audio, type View2D} from '@revideo/2d';
import {useLogger, useThread, waitFor, type ThreadGenerator} from '@revideo/core';
import {Caption} from './components/Caption';
import {findBeat, type Timing} from './timing';

/** Plays each beat's narration, shows its caption and keeps the beat on screen for its timed duration. */
export class Narrator {
  private readonly caption = new Caption({});

  public constructor(private readonly view: View2D, private readonly timing: Timing) {
    view.add(this.caption);
  }

  /** Seconds the beat stays on screen. */
  public duration(id: string): number {
    return findBeat(this.timing, id).duration;
  }

  /**
   * Runs the animations alongside the beat. The beat always lasts its timed duration so the picture stays in
   * sync with the narration; an animation that outlives its beat keeps running and is reported as a warning.
   */
  public *beat(id: string, ...animations: ThreadGenerator[]): ThreadGenerator {
    const beat = findBeat(this.timing, id);
    let audio: Audio | null = null;
    if (beat.audio) {
      audio = (<Audio src={beat.audio} play={true} />) as Audio;
      this.view.add(audio);
      // Resolves within the frame; reading media properties earlier logs an asynchronous property warning.
      yield audio;
    }
    this.caption.moveToTop();
    const deadline = useThread().time() + beat.duration;
    yield this.caption.show(beat.caption);
    for (const animation of animations) {
      yield watchDeadline(id, animation, deadline);
    }
    yield* waitFor(beat.duration);
    audio?.remove();
  }
}

function* watchDeadline(id: string, animation: ThreadGenerator, deadline: number): ThreadGenerator {
  yield* animation;
  const overrun = useThread().time() - deadline;
  if (overrun > 0.05) {
    useLogger().warn(`Beat '${id}': an animation ran ${overrun.toFixed(2)} s past the end of the beat`);
  }
}
