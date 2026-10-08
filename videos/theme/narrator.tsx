import {Audio, Txt, type View2D} from '@revideo/2d';
import {all, createRef, waitFor, type ThreadGenerator} from '@revideo/core';
import {palette} from './palette';
import {findBeat, type Timing} from './timing';

/**
 * Plays each beat's narration, shows its caption and keeps the beat on screen for its timed duration.
 * The theme plan replaces the plain caption with the Caption component.
 */
export class Narrator {
  private readonly caption = createRef<Txt>();

  public constructor(private readonly view: View2D, private readonly timing: Timing) {
    view.add(
      <Txt ref={this.caption} y={470} width={1600} textAlign={'center'} textWrap={true}
        fontSize={34} fill={palette.subtext} text={''} />,
    );
  }

  public *beat(id: string, ...animations: ThreadGenerator[]): ThreadGenerator {
    const beat = findBeat(this.timing, id);
    let audio: Audio | null = null;
    if (beat.audio) {
      audio = (<Audio src={beat.audio} play={true} />) as Audio;
      this.view.add(audio);
    }
    this.caption().text(beat.caption ?? '');
    yield* all(waitFor(beat.duration), ...animations);
    audio?.remove();
  }
}
