import {makeScene2D} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2} from '@revideo/core';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {arrive, bothMachines, machineAndStream, Pill} from './shared';

export default makeScene2D('hook', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Tracking', kicker: 'Every change, observed'});
  view.add(title);
  yield* narrator.beat('hook-title', title.enter());

  // The machine heats up on the left while its changes stream in on the right.
  const heat = machineAndStream('heat');
  camera.add(heat);
  yield heat.left;
  yield heat.right;
  const warm = heat.mark('warm');
  const ready = heat.mark('ready');
  const streamDuration = narrator.duration('hook-stream');
  yield* narrator.beat('hook-stream',
    title.exit(),
    delay(0.2, heat.arrive()),
    heat.play(streamDuration, {from: heat.mark('open'), to: warm}),
    delay(1.4, chain(
      camera.focusOn(() => heat.pagePoint(heat.left, 400, 300), {zoom: 1.1, duration: 2, clear: heat.frames}),
      camera.focusOn(() => heat.pagePoint(heat.right, 400, 420), {zoom: 1.12, duration: streamDuration - 3.6, clear: heat.frames}),
    )),
  );
  const derivedDuration = narrator.duration('hook-derived');
  yield* narrator.beat('hook-derived',
    heat.play(derivedDuration, {from: warm, to: ready + 1.5}),
    chain(
      camera.focusOn(() => heat.pagePoint(heat.right, 400, 138), {zoom: 1.35, duration: 1.8, clear: heat.frames}),
      camera.focusOn(() => heat.pagePoint(heat.left, 400, 142), {zoom: 1.25, duration: derivedDuration - 2, clear: heat.frames}),
    ),
  );

  // A failed brew leaves the plain machine stuck; the transactional one stays ready.
  const stuck = bothMachines('stuck');
  camera.add(stuck);
  yield stuck.left;
  yield stuck.right;
  const left = stuck.mark('left');
  const right = stuck.mark('right');
  const stuckDuration = narrator.duration('hook-stuck');
  yield* narrator.beat('hook-stuck',
    camera.reset(1),
    all(heat.opacity(0, 0.6, moveEasing), heat.scale(0.96, 0.6, moveEasing)),
    delay(0.3, stuck.arrive()),
    stuck.play(stuckDuration, {from: left - 1, to: right - 0.5}),
    delay(1.4, camera.focusOn(() => stuck.pagePoint(stuck.left, 400, 200), {zoom: 1.25, duration: stuckDuration - 1.6, clear: stuck.frames})),
  );
  heat.remove();

  const topics = ['Derived properties', 'Change streams', 'Lifecycle', 'Transactions'];
  const pills = topics.map((text, index) => new Pill({text, size: 28, x: -630 + index * 420, y: 400, opacity: 0}));
  pills.forEach(pill => camera.add(pill));
  const promiseDuration = narrator.duration('hook-promise');
  yield* narrator.beat('hook-promise',
    stuck.play(promiseDuration, {from: right - 0.5, to: right + 1.5}),
    chain(camera.reset(1.1), camera.focusOnPoint(new Vector2(0, 40), {zoom: 1.04, duration: promiseDuration - 1.3})),
    all(
      stuck.left.position(new Vector2(-420, -70), 1.1, moveEasing), stuck.left.scale(0.84, 1.1, moveEasing),
      stuck.right.position(new Vector2(420, -70), 1.1, moveEasing), stuck.right.scale(0.84, 1.1, moveEasing),
    ),
    delay(1.2, sequence(0.45, ...pills.map(pill => arrive(pill)))),
  );
});
