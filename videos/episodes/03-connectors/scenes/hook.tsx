import {makeScene2D} from '@revideo/2d';
import {all, chain, delay, loop, Vector2} from '@revideo/core';
import {Arrow} from '../../../theme/components/Arrow';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {arrive, MachineWindows, Pill, travel} from './shared';

export default makeScene2D('hook', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Connectors', kicker: 'Two processes, one machine'});
  view.add(title);
  yield* narrator.beat('hook-title', title.enter());

  const windows = new MachineWindows('brew');
  camera.add(windows);
  // Load both clips before the first frame that draws them.
  yield windows.server;
  yield windows.client;
  const click = windows.mark('click');
  const pressure = windows.mark('pressure');
  yield* narrator.beat('hook-windows',
    title.exit(),
    delay(0.2, windows.arrive()),
    windows.play(narrator.duration('hook-windows'), {from: windows.mark('open'), to: click - 0.2}),
    delay(1.4, chain(
      camera.focusOn(() => windows.pagePoint(windows.server, 400, 360), {zoom: 1.1, duration: 1.8}),
      camera.focusOn(() => windows.pagePoint(windows.client, 400, 360), {zoom: 1.1, duration: 2.2}),
    )),
  );

  const brewDuration = narrator.duration('hook-brew');
  yield* narrator.beat('hook-brew',
    windows.play(brewDuration, {from: click - 0.2, to: pressure + 0.6}),
    chain(
      camera.focusOn(() => windows.pagePoint(windows.client, 560, 470), {zoom: 1.35, duration: 1.4}),
      camera.focusOn(() => windows.pagePoint(windows.server, 400, 360), {zoom: 1.25, duration: brewDuration - 1.6}),
    ),
  );

  // A connector between the two windows, with values flowing both ways.
  const top = new Vector2(-150, -150);
  const topEnd = new Vector2(150, -150);
  const bottom = new Vector2(150, 10);
  const bottomEnd = new Vector2(-150, 10);
  const forward = new Arrow({curve: {p0: top, p1: top.add([100, 0]), p2: topEnd.add([-100, 0]), p3: topEnd}, color: palette.secondaryText});
  const backward = new Arrow({curve: {p0: bottom, p1: bottom.add([-100, 0]), p2: bottomEnd.add([100, 0]), p3: bottomEnd}, color: palette.secondaryText});
  const connector = new Pill({text: 'Connector', color: 'blue', y: -70, opacity: 0, scale: 0.9});
  camera.add(forward);
  camera.add(backward);
  camera.add(connector);
  const promiseDuration = narrator.duration('hook-promise');
  yield* narrator.beat('hook-promise',
    windows.play(promiseDuration, {from: pressure + 0.6}),
    chain(camera.reset(1.2), camera.focusOnPoint(new Vector2(0, -70), {zoom: 1.07, duration: promiseDuration - 1.4})),
    all(
      windows.server.x(-500, 1.2, moveEasing), windows.server.scale(0.86, 1.2, moveEasing),
      windows.client.x(500, 1.2, moveEasing), windows.client.scale(0.86, 1.2, moveEasing),
    ),
    delay(1.1, all(arrive(connector), forward.grow(0.7), backward.grow(0.7))),
    delay(1.8, loop(3, () => all(
      travel(camera, top, topEnd, 'purple', 0.9),
      delay(0.5, travel(camera, bottom, bottomEnd, 'cyan', 0.9)),
    ))),
  );
});
