import {makeScene2D, type Node} from '@revideo/2d';
import {all, chain, delay, loop, sequence, Vector2, waitFor} from '@revideo/core';
import configurationSource from '../sample/Client/Configuration.cs?raw';
import {Arrow} from '../../../theme/components/Arrow';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {messageCurve, type CubicCurve} from '../../../theme/geometry';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {enterEasing, moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {connectSteps, retryPath} from '../diagrams/flows';
import {arrive, leave, Pill, ride, toLocal, ValueCard} from './shared';

/** Space between a card edge and the arrow that links it. */
const linkGap = 10;

/** A gently bowed arrow from the right edge of one card to the left edge of another, at the given height. */
function link(from: Node & {width(): number; x(): number}, to: Node & {width(): number; x(): number}, y: number): CubicCurve {
  return messageCurve(from.x() + from.width() / 2 + linkGap, to.x() - to.width() / 2 - linkGap, y);
}

export default makeScene2D('sync', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Staying in sync', kicker: 'Chapter 4'});
  view.add(title);
  yield* narrator.beat('sync-title', title.enter());

  // Buffer, load, replay, reconcile.
  const steps = new FlowDiagram({definition: connectSteps, y: -100});
  camera.add(steps);
  yield* steps.build();
  const at = (id: string) => toLocal(camera, steps.node(id).absolutePosition());
  const buffer = at('buffer');
  const updates = ['Temperature 54', 'Pressure 0', 'Level 99'].map((text, index) =>
    new Pill({text, color: 'cyan', size: 22, x: buffer.x, y: buffer.y - 290 - index * 60, opacity: 0}));
  updates.forEach(update => camera.add(update));
  const stackY = (index: number) => buffer.y + 120 + index * 58;
  yield* narrator.beat('sync-buffer',
    title.exit(),
    delay(0.4, steps.reveal(0)),
    delay(0.8, camera.focusOnPoint(new Vector2(-380, 30), {zoom: 1.15, duration: narrator.duration('sync-buffer') - 1})),
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
    camera.focusOnPoint(new Vector2(-200, 20), {zoom: 1.15, duration: narrator.duration('sync-load') - 0.2}),
  );

  const replay = at('replay');
  yield* narrator.beat('sync-replay',
    steps.reveal(2),
    delay(0.9, sequence(0.45, ...updates.map((update, index) => update.position(new Vector2(replay.x, stackY(index)), 1, moveEasing)))),
    camera.focusOnPoint(new Vector2(0, 20), {zoom: 1.1, duration: narrator.duration('sync-replay') - 0.2}),
  );

  const reconcile = at('reconcile');
  const older = new Pill({text: 'Target 94', color: 'green', size: 22, x: reconcile.x, y: reconcile.y + 120, opacity: 0});
  const newer = new Pill({text: 'Target 95', color: 'green', size: 22, x: reconcile.x, y: reconcile.y + 180, opacity: 0});
  camera.add(older);
  camera.add(newer);
  yield* narrator.beat('sync-reconcile',
    steps.reveal(3),
    camera.focusOnPoint(new Vector2(250, 20), {zoom: 1.12, duration: narrator.duration('sync-reconcile') - 0.2}),
    delay(0.8, all(arrive(older), delay(0.3, arrive(newer)))),
    delay(2.6, all(older.opacity(0.25, 0.6), older.scale(0.85, 0.6, moveEasing))),
    delay(3.6, all(newer.x(reconcile.x + 380, 1.2, moveEasing), newer.opacity(0, 1.2, moveEasing))),
  );

  // A failed write waits in the retry queue.
  const retry = new FlowDiagram({definition: retryPath, y: -100});
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
    camera.focusOnPoint(new Vector2(120, -40), {zoom: 1.05, duration: narrator.duration('sync-retry-drain') - 0.2}),
    delay(0.8, sequence(0.5, ...writes.map(write => chain(
      write.position(new Vector2(source.x, lane), 0.4, moveEasing),
      write.position(new Vector2(external.x, lane), 0.7, moveEasing),
      write.opacity(0, 0.3),
    )))),
  );

  // The client's retry settings.
  const settings = new CodeCard({fileName: 'Client/Configuration.cs', width: 1560, height: 460, codeFontSize: 28, y: -60, opacity: 0, scale: 0.94});
  camera.add(settings);
  const memory = new Pill({text: 'in memory only', color: 'orange', size: 26, x: 0, y: 260, opacity: 0});
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
  const local = new ValueCard({label: 'Your model', value: '95 °C', color: 'purple', x: -440, opacity: 0, scale: 0.9});
  const remote = new ValueCard({label: 'External system', value: '93 °C', color: 'orange', x: 440, opacity: 0, scale: 0.9});
  const wins = new Arrow({curve: link(local, remote, 0)});
  camera.add(local);
  camera.add(remote);
  camera.add(wins);
  yield* narrator.beat('sync-local-wins',
    all(leave(settings, 0, -120), leave(memory, 0, -60)),
    delay(0.4, all(arrive(local), delay(0.2, arrive(remote)))),
    delay(2.6, wins.grow(1)),
    delay(3.7, remote.setValue('95 °C')),
    delay(1.4, camera.focusOnPoint(new Vector2(0, 0), {zoom: 1.1, duration: narrator.duration('sync-local-wins') - 1.6})),
  );
  settings.remove();
  memory.remove();
  const unknown = new Pill({text: 'older or newer?', color: 'pink', size: 28, x: 0, y: 80, opacity: 0});
  camera.add(unknown);
  yield* narrator.beat('sync-local-wins-why',
    arrive(unknown),
    loop(2, () => chain(unknown.y(64, 0.8, moveEasing), unknown.y(80, 0.8, moveEasing))),
    delay(3.6, all(unknown.opacity(0, 0.5), local.glow.opacity(0.14, 0.8, enterEasing), remote.glow.opacity(0.14, 0.8, enterEasing))),
    camera.focusOnPoint(new Vector2(0, 20), {zoom: 1.0, duration: narrator.duration('sync-local-wins-why') - 0.2}),
  );

  // Batching: each property collapses to its newest value within the buffer time, then rides the link.
  const model = new ValueCard({label: 'Your model', value: '91.8 °C', color: 'purple', x: -620, valueSize: 64, opacity: 0, scale: 0.9});
  const target = new ValueCard({label: 'External system', value: '91.8 °C', color: 'orange', x: 620, valueSize: 64, opacity: 0, scale: 0.9});
  const outbound = new Arrow({curve: link(model, target, 0)});
  const bufferLabel = new Pill({text: 'buffer time 8 ms', size: 22, y: 48, opacity: 0});
  const values = ['92.1 °C', '92.6 °C', '93.0 °C'];
  const chip = (value: string, color: 'pink' | 'orange' = 'pink') => new Pill({text: value, color, size: 26, x: outbound.line.p0().x, opacity: 0});
  const chips = values.map(value => chip(value));
  [model, target, outbound, bufferLabel, ...chips].forEach(node => camera.add(node));
  /** A change leaves the model and waits in the buffer halfway along the link; it replaces the change before it. */
  const buffered = (index: number) => all(
    model.setValue(values[index], 0.3),
    chips[index].opacity(1, 0.3),
    ride(chips[index], outbound, 0, 0.5, 0.8),
    index === 0 ? waitFor(0) : delay(0.3, all(chips[index - 1].opacity(0, 0.35), chips[index - 1].scale(0.7, 0.35, moveEasing))),
  );
  yield* narrator.beat('sync-batching',
    all(leave(local, 0, -100), leave(remote, 0, -100), wins.opacity(0, 0.5), leave(unknown, 0, 60)),
    delay(0.5, all(arrive(model), arrive(target))),
    delay(0.9, outbound.grow(0.8)),
    delay(1.3, arrive(bufferLabel)),
    delay(1.7, sequence(0.9, ...values.map((_, index) => buffered(index)))),
    delay(5.0, chain(chips[2].scale(1.12, 0.3).to(1, 0.4), bufferLabel.scale(1.08, 0.3).to(1, 0.4))),
    delay(1.0, camera.focusOnPoint(new Vector2(0, 0), {zoom: 1.06, duration: narrator.duration('sync-batching') - 1.2})),
  );
  yield* narrator.beat('sync-settled',
    ride(chips[2], outbound, 0.5, 1, 1.2),
    delay(1.1, all(chips[2].opacity(0, 0.3), chips[2].scale(0.8, 0.3, moveEasing))),
    delay(1.2, target.setValue('93.0 °C')),
    camera.focusOnPoint(new Vector2(300, 0), {zoom: 1.1, duration: narrator.duration('sync-settled') - 0.2}),
  );

  // Without a buffer time, every change rides the link on its own.
  const unbuffered = ['93.1 °C', '93.2 °C', '93.3 °C'];
  const single = unbuffered.map(value => chip(value));
  single.forEach(node => camera.add(node));
  yield* narrator.beat('sync-zero-buffer',
    camera.reset(1),
    delay(0.2, bufferLabel.retext('buffer time 0 ms')),
    delay(0.6, sequence(0.9, ...single.map((node, index) => all(
      model.setValue(unbuffered[index], 0.3),
      node.opacity(1, 0.2),
      chain(ride(node, outbound, 0, 1, 1.0), all(node.opacity(0, 0.3), target.setValue(unbuffered[index], 0.3))),
    )))),
  );

  // A value from the source comes in on the way back and is not echoed out again.
  const inbound = new Arrow({curve: {p0: {x: 400 - linkGap, y: 60}, p1: {x: 130, y: 140}, p2: {x: -130, y: 140}, p3: {x: -400 + linkGap, y: 60}}});
  const echo = new Arrow({curve: {p0: {x: -400 + linkGap, y: -60}, p1: {x: -130, y: -140}, p2: {x: 130, y: -140}, p3: {x: 400 - linkGap, y: -60}}, color: palette.pink, dashed: true});
  const incoming = chip('93.4 °C', 'orange');
  incoming.position(inbound.line.p0());
  camera.add(inbound);
  camera.add(echo);
  camera.add(incoming);
  yield* narrator.beat('sync-echo',
    bufferLabel.opacity(0, 0.4),
    inbound.grow(0.6),
    delay(0.2, all(target.setValue('93.4 °C', 0.3), incoming.opacity(1, 0.3))),
    delay(0.4, ride(incoming, inbound, 0, 1, 1.2)),
    delay(1.6, all(incoming.opacity(0, 0.3), model.setValue('93.4 °C', 0.3))),
    delay(2.0, echo.line.end(0.45, 0.6, moveEasing)),
    delay(2.7, echo.opacity(0, 0.6)),
  );

  // Source transactions: the external system confirms before the model changes.
  const confirmed = new CodeCard({fileName: 'Client/Configuration.cs', width: 1560, height: 720, codeFontSize: 28, opacity: 0, scale: 0.94});
  camera.add(confirmed);
  const confirmedFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => confirmed.linesCenter(from, to).add(confirmed.absolutePosition()).scale(0.5), {zoom, duration});
  yield* narrator.beat('sync-transactions',
    camera.reset(1),
    all(...[model, target, outbound, inbound, echo, bufferLabel, ...chips, ...single, incoming].map(node => node.opacity(0, 0.6))),
    delay(0.5, arrive(confirmed, 0.94)),
    delay(0.8, confirmed.show(extractRegion(configurationSource, 'ConfirmedWrite'), 3)),
    delay(4.4, confirmed.focus(4, 5)),
    delay(4.6, confirmedFocus(4, 5, 1.12, 3)),
  );
  [model, target, outbound, inbound, echo, bufferLabel, ...chips, ...single, incoming, local, remote, wins, unknown].forEach(node => node.remove());
  const commitDuration = narrator.duration('sync-transactions-commit');
  yield* narrator.beat('sync-transactions-commit',
    confirmed.focus(11, 11),
    confirmedFocus(11, 11, 1.2, 1.8),
    delay(3.2, confirmed.focus(12, 12)),
    delay(3.2, confirmedFocus(12, 12, 1.2, commitDuration - 3.4)),
  );

  const localFirst = new ValueCard({label: 'Local first', value: 'applied at once', color: 'green', width: 620, height: 210, valueSize: 46, x: 520, y: -130, opacity: 0, scale: 0.9});
  const transaction = new ValueCard({label: 'Transaction', value: 'after confirmation', color: 'blue', width: 620, height: 210, valueSize: 46, x: 520, y: 130, opacity: 0, scale: 0.9});
  camera.add(localFirst);
  camera.add(transaction);
  yield* narrator.beat('sync-tradeoff',
    camera.reset(1),
    confirmed.unfocus(),
    all(confirmed.scale(0.56, 1, moveEasing), confirmed.x(-420, 1, moveEasing)),
    delay(0.7, arrive(localFirst)),
    delay(1.6, arrive(transaction)),
    delay(2.4, chain(waitFor(0.2), localFirst.scale(1.04, 0.3).to(1, 0.4), transaction.scale(1.04, 0.3).to(1, 0.4))),
  );
});
