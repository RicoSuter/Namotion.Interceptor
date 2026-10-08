import {makeScene2D, Rect} from '@revideo/2d';
import {all, chain, delay, loop, sequence, Vector2, waitFor} from '@revideo/core';
import configurationSource from '../sample/Client/Configuration.cs?raw';
import {Arrow} from '../../../theme/components/Arrow';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {enterEasing, moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {connectSteps, retryPath} from '../diagrams/flows';
import {arrive, leave, Pill, toLocal, ValueCard} from './shared';

export default makeScene2D('sync', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Staying in sync', kicker: 'Chapter 4'});
  view.add(title);
  yield* narrator.beat('sync-title', title.enter());

  // Buffer, load, replay, reconcile.
  const steps = new FlowDiagram({definition: connectSteps, y: -180});
  camera.add(steps);
  yield* steps.build();
  const at = (id: string) => toLocal(camera, steps.node(id).absolutePosition());
  const buffer = at('buffer');
  const updates = ['Temperature 54', 'Pressure 0', 'Level 99'].map((text, index) =>
    new Pill({text, color: 'cyan', size: 22, x: buffer.x, y: -470 - index * 60, opacity: 0}));
  updates.forEach(update => camera.add(update));
  const stackY = (index: number) => buffer.y + 120 + index * 58;
  yield* narrator.beat('sync-buffer',
    title.exit(),
    delay(0.4, steps.reveal(0)),
    delay(0.8, camera.focusOnPoint(new Vector2(-380, -60), {zoom: 1.15, duration: narrator.duration('sync-buffer') - 1})),
    delay(1.4, sequence(0.5, ...updates.map((update, index) => all(
      update.opacity(1, 0.3),
      update.y(stackY(index), 0.9, moveEasing),
    )))),
  );

  const load = at('load');
  const welcome = new Pill({text: 'Welcome: full state', color: 'purple', size: 24, x: 760, y: load.y + 140, opacity: 0});
  camera.add(welcome);
  yield* narrator.beat('sync-load',
    steps.reveal(1),
    delay(0.8, all(welcome.opacity(1, 0.4), welcome.x(load.x, 1.4, moveEasing))),
    camera.focusOnPoint(new Vector2(-200, -60), {zoom: 1.15, duration: narrator.duration('sync-load') - 0.2}),
  );

  const replay = at('replay');
  yield* narrator.beat('sync-replay',
    steps.reveal(2),
    delay(0.9, sequence(0.45, ...updates.map((update, index) => update.position(new Vector2(replay.x, stackY(index)), 1, moveEasing)))),
    camera.focusOnPoint(new Vector2(0, -60), {zoom: 1.1, duration: narrator.duration('sync-replay') - 0.2}),
  );

  const reconcile = at('reconcile');
  const older = new Pill({text: 'Target 94', color: 'green', size: 22, x: reconcile.x, y: reconcile.y + 120, opacity: 0});
  const newer = new Pill({text: 'Target 95', color: 'green', size: 22, x: reconcile.x, y: reconcile.y + 180, opacity: 0});
  camera.add(older);
  camera.add(newer);
  yield* narrator.beat('sync-reconcile',
    steps.reveal(3),
    camera.focusOnPoint(new Vector2(250, -60), {zoom: 1.12, duration: narrator.duration('sync-reconcile') - 0.2}),
    delay(0.8, all(arrive(older), delay(0.3, arrive(newer)))),
    delay(2.6, all(older.opacity(0.25, 0.6), older.scale(0.85, 0.6, moveEasing))),
    delay(3.6, all(newer.x(reconcile.x + 380, 1.2, moveEasing), newer.opacity(0, 1.2, moveEasing))),
  );

  // A failed write waits in the retry queue.
  const retry = new FlowDiagram({definition: retryPath, y: -230});
  camera.add(retry);
  yield* retry.build();
  const queue = toLocal(camera, retry.node('queue').absolutePosition());
  const source = toLocal(camera, retry.node('source').absolutePosition());
  const external = toLocal(camera, retry.node('external').absolutePosition());
  const offline = new Pill({text: 'offline', color: 'pink', size: 22, x: (source.x + external.x) / 2, y: source.y - 60, opacity: 0});
  const lane = source.y + 100;
  const parkY = (index: number) => source.y + 190 + index * 56;
  const retryLabel = new Pill({text: 'write retry queue', color: 'blue', size: 24, x: source.x - 300, y: parkY(1), opacity: 0});
  const writes = ['Target 95', 'Grind 6', 'State Brewing'].map(text =>
    new Pill({text, color: 'purple', size: 22, x: queue.x, y: lane, opacity: 0}));
  camera.add(offline);
  camera.add(retryLabel);
  writes.forEach(write => camera.add(write));
  yield* narrator.beat('sync-retry',
    camera.reset(1),
    all(leave(steps, 0, -80), ...[...updates, older, newer, welcome].map(node => leave(node, 0, -80))),
    delay(0.5, all(retry.reveal(0))),
    delay(1.4, arrive(offline)),
    delay(1.8, arrive(retryLabel)),
    delay(2.2, sequence(0.5, ...writes.map((write, index) => chain(
      all(write.opacity(1, 0.3), write.position(new Vector2(source.x, lane), 0.8, moveEasing)),
      write.position(new Vector2(source.x, parkY(index)), 0.6, moveEasing),
    )))),
  );
  steps.remove();
  [...updates, older, newer, welcome].forEach(node => node.remove());
  yield* narrator.beat('sync-retry-drain',
    offline.retext('online'),
    camera.focusOnPoint(new Vector2(250, -80), {zoom: 1.1, duration: narrator.duration('sync-retry-drain') - 0.2}),
    delay(0.8, sequence(0.5, ...writes.map(write => chain(
      write.position(new Vector2(source.x, lane), 0.4, moveEasing),
      write.position(new Vector2(external.x, lane), 0.7, moveEasing),
      write.opacity(0, 0.3),
    )))),
  );

  // The client's retry settings.
  const settings = new CodeCard({fileName: 'Client/Configuration.cs', width: 1560, height: 460, codeFontSize: 28, y: -120, opacity: 0, scale: 0.94});
  camera.add(settings);
  const memory = new Pill({text: 'in memory only', color: 'orange', size: 26, x: 0, y: 200, opacity: 0});
  camera.add(memory);
  yield* narrator.beat('sync-retry-config',
    camera.reset(1),
    all(leave(retry, -200, 0), leave(offline, -200, 0), leave(retryLabel, -200, 0)),
    delay(0.4, arrive(settings, 0.94)),
    delay(0.7, settings.show(extractRegion(configurationSource, 'RetryQueue'), 2)),
    delay(3, settings.focus(4, 4)),
    delay(3.2, camera.focusOn(() => settings.linesCenter(4, 4).add(settings.absolutePosition()).scale(0.5), {zoom: 1.15, duration: 2.8})),
  );
  retry.remove();
  offline.remove();
  retryLabel.remove();
  yield* narrator.beat('sync-retry-memory',
    camera.reset(1.4),
    delay(0.6, arrive(memory)),
    delay(1.2, settings.focus(4, 5)),
    delay(2.6, memory.scale(1.1, 0.3).to(1, 0.4)),
  );

  // A committed local write wins over the older value of the external system.
  const local = new ValueCard({label: 'Your model', value: '95 °C', color: 'purple', x: -440, y: -140, opacity: 0, scale: 0.9});
  const remote = new ValueCard({label: 'External system', value: '93 °C', color: 'orange', x: 440, y: -140, opacity: 0, scale: 0.9});
  const wins = new Arrow({curve: {p0: {x: -200, y: -140}, p1: {x: -60, y: -140}, p2: {x: 60, y: -140}, p3: {x: 200, y: -140}}, color: palette.green, lineWidth: 5});
  camera.add(local);
  camera.add(remote);
  camera.add(wins);
  yield* narrator.beat('sync-local-wins',
    all(leave(settings, 0, -120), leave(memory, 0, -60)),
    delay(0.4, all(arrive(local), delay(0.2, arrive(remote)))),
    delay(2.6, wins.grow(1)),
    delay(3.7, remote.setValue('95 °C')),
    delay(1.4, camera.focusOnPoint(new Vector2(0, -140), {zoom: 1.1, duration: narrator.duration('sync-local-wins') - 1.6})),
  );
  settings.remove();
  memory.remove();
  const unknown = new Pill({text: 'older or newer?', color: 'pink', size: 28, x: 0, y: 40, opacity: 0});
  camera.add(unknown);
  yield* narrator.beat('sync-local-wins-why',
    arrive(unknown),
    loop(2, () => chain(unknown.y(20, 0.8, moveEasing), unknown.y(40, 0.8, moveEasing))),
    delay(3.6, all(unknown.opacity(0, 0.5), local.glow.opacity(0.14, 0.8, enterEasing), remote.glow.opacity(0.14, 0.8, enterEasing))),
    camera.focusOnPoint(new Vector2(0, -100), {zoom: 1.0, duration: narrator.duration('sync-local-wins-why') - 0.2}),
  );

  // Batching: each property collapses to its newest value within the buffer time.
  const model = new ValueCard({label: 'Your model', value: '93.0 °C', color: 'purple', x: -620, y: -140, valueSize: 64, opacity: 0, scale: 0.9});
  const target = new ValueCard({label: 'External system', value: '91.8 °C', color: 'orange', x: 620, y: -140, valueSize: 64, opacity: 0, scale: 0.9});
  const values = ['92.1 °C', '92.6 °C', '93.0 °C'];
  const chips = values.map((value, index) => new Pill({text: value, color: 'pink', size: 26, x: -230 + index * 230, y: -140, opacity: 0}));
  const timerTrack = new Rect({width: 600, height: 10, radius: 5, fill: palette.elevated, y: -40, opacity: 0});
  const timer = new Rect({width: 0, height: 10, radius: 5, fill: palette.pink, x: -300, offset: [-1, 0], y: -40, opacity: 0});
  const timerLabel = new Pill({text: 'buffer time 8 ms', size: 22, y: 20, opacity: 0});
  const send = new Arrow({curve: {p0: {x: 230, y: -140}, p1: {x: 300, y: -140}, p2: {x: 340, y: -140}, p3: {x: 395, y: -140}}, color: palette.secondaryText});
  [model, target, timerTrack, timer, timerLabel, send, ...chips].forEach(node => camera.add(node));
  yield* narrator.beat('sync-batching',
    all(leave(local, 0, -100), leave(remote, 0, -100), wins.opacity(0, 0.5), leave(unknown, 0, 60)),
    delay(0.5, all(arrive(model), arrive(target))),
    delay(1.0, all(timerTrack.opacity(1, 0.4), timer.opacity(1, 0.4), arrive(timerLabel))),
    delay(1.3, sequence(0.6, ...chips.map(chip => arrive(chip)))),
    delay(1.3, timer.width(600, 2.6, moveEasing)),
    delay(4.4, all(
      ...chips.slice(0, 2).map(chip => all(chip.x(230, 0.9, moveEasing), chip.opacity(0, 0.9, moveEasing))),
      chips[2].scale(1.12, 0.3).to(1, 0.4),
    )),
  );
  yield* narrator.beat('sync-settled',
    send.grow(0.6),
    delay(0.5, all(chips[2].x(520, 1, moveEasing), chips[2].opacity(0, 1, moveEasing))),
    delay(1.4, target.setValue('93.0 °C')),
    all(timer.opacity(0, 0.6), timerTrack.opacity(0, 0.6), timerLabel.opacity(0, 0.6)),
    camera.focusOnPoint(new Vector2(300, -140), {zoom: 1.1, duration: narrator.duration('sync-settled') - 0.2}),
  );
  const single = values.map((value, index) => new Pill({text: value, color: 'pink', size: 26, x: -230 + index * 230, y: -140, opacity: 0}));
  single.forEach(chip => camera.add(chip));
  yield* narrator.beat('sync-zero-buffer',
    camera.reset(1),
    sequence(0.25, ...single.map(chip => arrive(chip))),
    delay(0.9, sequence(0.9, ...single.map((chip, index) => chain(
      all(chip.x(520, 0.8, moveEasing), chip.opacity(0, 0.8, moveEasing)),
      target.setValue(values[index], 0.3),
    )))),
  );
  const inbound = new Pill({text: '93.4 °C', color: 'orange', size: 26, x: 520, y: -10, opacity: 0});
  const echo = new Arrow({curve: {p0: {x: -400, y: 40}, p1: {x: -100, y: 90}, p2: {x: 100, y: 90}, p3: {x: 400, y: 40}}, color: palette.pink, dashed: true});
  camera.add(inbound);
  camera.add(echo);
  yield* narrator.beat('sync-echo',
    arrive(inbound),
    delay(0.4, all(inbound.x(-520, 1.2, moveEasing), target.setValue('93.4 °C', 0.3))),
    delay(1.6, all(inbound.opacity(0, 0.3), model.setValue('93.4 °C', 0.3))),
    delay(2.0, echo.grow(0.8)),
    delay(2.9, echo.opacity(0, 0.6)),
  );

  // Source transactions: the external system confirms before the model changes.
  const confirmed = new CodeCard({fileName: 'Client/Configuration.cs', width: 1560, height: 720, codeFontSize: 28, y: -70, opacity: 0, scale: 0.94});
  camera.add(confirmed);
  const confirmedFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => confirmed.linesCenter(from, to).add(confirmed.absolutePosition()).scale(0.5), {zoom, duration});
  yield* narrator.beat('sync-transactions',
    camera.reset(1),
    all(...[model, target, send, ...chips, ...single, inbound, echo, timer, timerTrack, timerLabel].map(node => node.opacity(0, 0.6))),
    delay(0.5, arrive(confirmed, 0.94)),
    delay(0.8, confirmed.show(extractRegion(configurationSource, 'ConfirmedWrite'), 3)),
    delay(4.4, confirmed.focus(4, 5)),
    delay(4.6, confirmedFocus(4, 5, 1.12, 3)),
  );
  [model, target, send, ...chips, ...single, inbound, echo, timer, timerTrack, timerLabel, local, remote, wins, unknown].forEach(node => node.remove());
  const commitDuration = narrator.duration('sync-transactions-commit');
  yield* narrator.beat('sync-transactions-commit',
    confirmed.focus(11, 11),
    confirmedFocus(11, 11, 1.2, 1.8),
    delay(3.2, confirmed.focus(12, 12)),
    delay(3.2, confirmedFocus(12, 12, 1.2, commitDuration - 3.4)),
  );

  const localFirst = new ValueCard({label: 'Local first', value: 'applied at once', color: 'green', width: 620, height: 210, valueSize: 46, x: 520, y: -230, opacity: 0, scale: 0.9});
  const transaction = new ValueCard({label: 'Transaction', value: 'after confirmation', color: 'blue', width: 620, height: 210, valueSize: 46, x: 520, y: 30, opacity: 0, scale: 0.9});
  camera.add(localFirst);
  camera.add(transaction);
  yield* narrator.beat('sync-tradeoff',
    camera.reset(1),
    confirmed.unfocus(),
    all(confirmed.scale(0.56, 1, moveEasing), confirmed.x(-420, 1, moveEasing), confirmed.y(-100, 1, moveEasing)),
    delay(0.7, arrive(localFirst)),
    delay(1.6, arrive(transaction)),
    delay(2.4, chain(waitFor(0.2), localFirst.scale(1.04, 0.3).to(1, 0.4), transaction.scale(1.04, 0.3).to(1, 0.4))),
  );
});
