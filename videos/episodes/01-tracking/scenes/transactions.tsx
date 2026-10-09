import {makeScene2D, Rect} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2, waitFor, type ThreadGenerator} from '@revideo/core';
import boilerSource from '../../../domain/Coffee/Boiler.cs?raw';
import machineSource from '../../../domain/Coffee/CoffeeMachine.cs?raw';
import hostSource from '../sample/MachineHost.cs?raw';
import stepsSource from '../sample/Steps.cs?raw';
import {Arrow} from '../../../theme/components/Arrow';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {enterEasing, moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {arrive, bothMachines, Box, leave, nudge, Pill, toLocal} from './shared';

/** The four writes of a brew, as chips whose dot shows their state: grey pending, green landed, pink failed. */
const writeNames = ['State', 'ActiveRecipeName', 'TargetTemperature', 'IsRunning'];

function writeChips(): Pill[] {
  return writeNames.map(name => {
    const chip = new Pill({text: name, code: true, color: 'green', size: 26, opacity: 0});
    chip.dot!.fill(palette.edge);
    return chip;
  });
}

function* recolor(chips: Pill[], color: string, duration = 0.4): ThreadGenerator {
  yield* all(...chips.map(chip => chip.recolor(color, duration)));
}

export default makeScene2D('transactions', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Transactions', kicker: 'Chapter 5'});
  view.add(title);
  yield* narrator.beat('tx-title', title.enter());

  // Brew: a ready check, then four separate writes.
  const code = new CodeCard({fileName: 'CoffeeMachine.cs', width: 1560, height: 800, codeFontSize: 26, opacity: 0, scale: 0.94});
  camera.add(code);
  const lineFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => code.linesCenter(from, to).add(code.absolutePosition()).scale(0.5), {zoom, duration, clear: code});
  const brewDuration = narrator.duration('tx-brew');
  yield* narrator.beat('tx-brew',
    title.exit(),
    delay(0.3, arrive(code, 0.94)),
    delay(0.6, code.show(extractRegion(machineSource, 'Brew'), 2)),
    delay(2.8, code.focus(2, 5)),
    delay(brewDuration * 0.62, code.focus(8, 11)),
    delay(brewDuration * 0.62, lineFocus(8, 11, 1.1, brewDuration * 0.38 - 0.2)),
  );

  // Each write lands on its own.
  const chips = writeChips();
  const columnX = 500;
  const rowY = (index: number) => -180 + index * 120;
  chips.forEach((chip, index) => {
    chip.position(new Vector2(columnX + 80, rowY(index)));
    camera.add(chip);
  });
  const separateDuration = narrator.duration('tx-separate');
  yield* narrator.beat('tx-separate',
    camera.reset(1),
    all(code.x(-470, 1, moveEasing), code.scale(0.58, 1, moveEasing)),
    delay(0.9, sequence(0.2, ...chips.map(chip => all(chip.opacity(1, 0.4), chip.x(columnX, 0.6, enterEasing))))),
    delay(2.2, sequence(Math.max((separateDuration - 3.4) / 4, 0.4), ...chips.map(chip => all(chip.recolor('green'), nudge(chip, 1.08))))),
  );

  // The Ristretto asks for 97 °C; the boiler accepts 85 to 96.
  const ristretto = new Pill({text: 'Ristretto 97 °C', color: 'pink', size: 26, opacity: 0});
  camera.add(ristretto);
  const ristrettoDuration = narrator.duration('tx-ristretto');
  yield* narrator.beat('tx-ristretto',
    recolor(chips, palette.edge),
    code.morph(extractRegion(boilerSource, 'Boiler'), 1.2, 'Boiler.cs'),
    delay(1.4, code.focus(5, 6)),
    delay(1.6, (function* () {
      ristretto.position(toLocal(camera, code.linesCenter(5, 6)).add([60, 120]));
      yield* arrive(ristretto);
    })()),
    delay(1.6, camera.focusOnPoint(new Vector2(-250, 0), {zoom: 1.12, duration: ristrettoDuration - 1.8})),
  );

  // The third write throws: two writes landed, the pump never starts.
  const error = new Pill({text: 'ValidationException', color: 'pink', size: 24, opacity: 0});
  const never = new Pill({text: 'never runs', size: 24, opacity: 0});
  camera.add(error);
  camera.add(never);
  const failDuration = narrator.duration('tx-fail');
  yield* narrator.beat('tx-fail',
    camera.reset(1),
    all(leave(code, -200, 0, 0.8), leave(ristretto, -200, 0, 0.8)),
    delay(0.3, all(...chips.map(chip => chip.x(-120, 1, moveEasing)))),
    delay(1.4, chain(
      all(chips[0].recolor('green'), nudge(chips[0], 1.08)),
      all(chips[1].recolor('green'), nudge(chips[1], 1.08)),
      waitFor(0.3),
      all(chips[2].recolor('pink'), nudge(chips[2], 1.1), (function* () {
        error.position(new Vector2(330, rowY(2)));
        yield* arrive(error);
      })()),
    )),
    delay(failDuration * 0.7, (function* () {
      never.position(new Vector2(250, rowY(3)));
      yield* all(arrive(never), chips[3].opacity(0.45, 0.5));
    })()),
    delay(1.2, camera.focusOnPoint(new Vector2(60, 0), {zoom: 1.12, duration: failDuration - 1.4})),
  );

  // Live: the plain Brew gets stuck.
  const machines = bothMachines('stuck');
  camera.add(machines);
  yield machines.left;
  yield machines.right;
  const left = machines.mark('left');
  const right = machines.mark('right');
  const espresso = machines.mark('espresso');
  const pressure = machines.mark('pressure');
  const liveDuration = narrator.duration('tx-stuck-live');
  yield* narrator.beat('tx-stuck-live',
    camera.reset(1),
    all(...[...chips, error, never].map(node => leave(node, 0, -60, 0.6))),
    delay(0.3, machines.arrive()),
    machines.play(liveDuration, {from: left - 2.5, to: left + 1.5}),
    delay(1.2, camera.focusOn(() => machines.pagePoint(machines.left, 300, 520), {zoom: 1.3, duration: liveDuration - 1.4, clear: machines.frames})),
  );
  code.remove();
  ristretto.remove();
  const stuck = new Pill({text: 'stuck', color: 'pink', size: 28, opacity: 0});
  camera.add(stuck);
  const stuckDuration = narrator.duration('tx-stuck');
  yield* narrator.beat('tx-stuck',
    machines.play(stuckDuration, {from: left + 1.5, to: right - 1.6}),
    camera.focusOn(() => machines.pagePoint(machines.left, 400, 300), {zoom: 1.35, duration: 1.4, clear: machines.frames}),
    delay(1.8, (function* () {
      stuck.position(toLocal(camera, machines.pagePoint(machines.left, 640, 142)));
      yield* arrive(stuck);
    })()),
  );
  const alarm = (
    <Rect width={() => machines.left.width() * machines.left.scale().x + 24} height={() => machines.left.height() * machines.left.scale().y + 24}
      radius={34} stroke={palette.pink} lineWidth={5} opacity={0} />
  ) as Rect;
  camera.add(alarm);
  const whyDuration = narrator.duration('tx-why');
  yield* narrator.beat('tx-why',
    machines.play(whyDuration, {from: right - 1.6, to: right - 0.2}),
    leave(stuck, 0, -20, 0.5),
    camera.focusOnPoint(new Vector2(-200, 0), {zoom: 1.02, duration: 1.4, clear: machines.frames}),
    delay(1.4, (function* () {
      alarm.position(machines.left.position());
      yield* chain(alarm.opacity(0.9, 0.5), alarm.opacity(0.3, 0.7), alarm.opacity(0.9, 0.5), alarm.opacity(0, whyDuration - 3.3));
    })()),
  );
  stuck.remove();

  // The fix: transactions on the context, then BrewAsync.
  const fix = new CodeCard({fileName: 'Steps.cs', width: 1560, height: 800, codeFontSize: 26, opacity: 0, scale: 0.94});
  camera.add(fix);
  const fixFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => fix.linesCenter(from, to).add(fix.absolutePosition()).scale(0.5), {zoom, duration, clear: fix});
  const contextDuration = narrator.duration('tx-context');
  yield* narrator.beat('tx-context',
    camera.reset(1),
    all(machines.opacity(0, 0.7, moveEasing), machines.scale(0.9, 0.8, moveEasing), alarm.opacity(0, 0.3)),
    delay(0.4, arrive(fix, 0.94)),
    delay(0.6, fix.show(extractRegion(stepsSource, 'Context'), 1.2)),
    delay(contextDuration * 0.5, fix.morph(extractRegion(hostSource, 'ContextWithTransactions'), 1.2, 'MachineHost.cs')),
    delay(contextDuration * 0.5 + 1.4, fix.focus(4, 4)),
    delay(contextDuration * 0.5 + 1.4, fixFocus(4, 4, 1.2, contextDuration * 0.5 - 1.6)),
  );
  alarm.remove();
  const asyncDuration = narrator.duration('tx-async-code');
  yield* narrator.beat('tx-async-code',
    camera.reset(1),
    delay(0.2, fix.morph(extractRegion(machineSource, 'BrewAsync'), 1.6, 'CoffeeMachine.cs')),
    delay(2.2, fix.focus(3, 4)),
    delay(2.4, fixFocus(3, 4, 1.12, asyncDuration - 2.6)),
  );
  yield* narrator.beat('tx-subject-context',
    fix.focus(2, 2),
    fixFocus(2, 2, 1.25, narrator.duration('tx-subject-context') - 0.2),
  );

  // Inside the transaction, writes are captured instead of applied.
  const pending = new Box({label: 'pending writes', width: 600, height: 470, x: 470, y: -60, opacity: 0});
  const model = new Pill({text: 'CoffeeMachine: Ready', color: 'purple', size: 26, x: 470, y: 330, opacity: 0});
  camera.add(pending);
  camera.add(model);
  const captured = writeChips();
  const slotY = (index: number) => -200 + index * 92;
  captured.forEach((chip, index) => {
    chip.position(new Vector2(470, slotY(index) - 120));
    camera.add(chip);
  });
  const captureDuration = narrator.duration('tx-capture');
  yield* narrator.beat('tx-capture',
    camera.reset(1),
    all(fix.x(-480, 1, moveEasing), fix.scale(0.56, 1, moveEasing)),
    delay(0.3, fix.focus(12, 15)),
    delay(0.8, all(arrive(pending), delay(0.3, arrive(model)))),
    delay(1.6, sequence(Math.max((captureDuration - 2.8) / 4, 0.35), ...captured.map((chip, index) => all(chip.opacity(1, 0.3), chip.y(slotY(index), 0.6, enterEasing))))),
  );
  const read = new Pill({text: 'IsReady sees pending values', color: 'blue', size: 24, x: 470, y: -400, opacity: 0});
  camera.add(read);
  const readArrow = new Arrow({curve: {p0: {x: 170, y: -200}, p1: {x: 80, y: -260}, p2: {x: 120, y: -380}, p3: {x: 210, y: -400}}, color: palette.blue});
  camera.add(readArrow);
  const readDuration = narrator.duration('tx-read');
  yield* narrator.beat('tx-read',
    fix.focus(6, 9),
    delay(0.8, readArrow.grow(0.8)),
    delay(1.4, arrive(read)),
    camera.focusOnPoint(new Vector2(150, -120), {zoom: 1.06, duration: readDuration - 0.2}),
  );

  // Commit replays the writes in order; each lands and publishes its change.
  const commitDuration = narrator.duration('tx-commit');
  const landing = (index: number) => all(
    captured[index].position(new Vector2(470, 330), 0.6, moveEasing),
    delay(0.45, all(captured[index].opacity(0, 0.2), nudge(model, 1.06))),
  );
  yield* narrator.beat('tx-commit',
    all(leave(read, 0, -20, 0.5), readArrow.opacity(0, 0.4)),
    fix.focus(17, 17),
    delay(1.2, sequence(Math.max((commitDuration - 2.6) / 4, 0.5), ...captured.map((_, index) => chain(captured[index].recolor('green', 0.3), landing(index))))),
    delay(commitDuration * 0.55, model.retext('CoffeeMachine: Brewing')),
    camera.focusOnPoint(new Vector2(200, 120), {zoom: 1.06, duration: commitDuration - 0.2}),
  );
  read.remove();
  readArrow.remove();

  // Observers see every step; the guarantee is all or nothing.
  const line = writeChips();
  line.forEach((chip, index) => {
    chip.position(new Vector2(-560 + index * 380, 150));
    chip.dot!.fill(palette.green);
    camera.add(chip);
  });
  const allOrNothing = new Pill({text: 'all or nothing', color: 'green', size: 32, x: 0, y: -40, opacity: 0});
  camera.add(allOrNothing);
  const observersDuration = narrator.duration('tx-observers');
  yield* narrator.beat('tx-observers',
    camera.reset(1),
    all(leave(fix, -200, 0, 0.8), leave(pending, 200, 0, 0.8), leave(model, 200, 0, 0.8)),
    delay(0.6, sequence(0.25, ...line.map(chip => arrive(chip)))),
    delay(observersDuration * 0.55, arrive(allOrNothing)),
    delay(1.6, camera.focusOnPoint(new Vector2(0, 60), {zoom: 1.08, duration: observersDuration - 1.8})),
  );
  fix.remove();
  captured.forEach(chip => chip.remove());

  // A throw before the commit discards every captured write.
  const discarded = new Box({label: 'pending writes', width: 600, height: 470, x: 0, y: -40, opacity: 0});
  const untouched = new Pill({text: 'CoffeeMachine: Ready', color: 'purple', size: 26, x: 0, y: 330, opacity: 0});
  camera.add(discarded);
  camera.add(untouched);
  const disposeDuration = narrator.duration('tx-dispose');
  yield* narrator.beat('tx-dispose',
    camera.reset(1),
    leave(allOrNothing, 0, -40, 0.5),
    all(arrive(discarded), delay(0.2, arrive(untouched))),
    delay(0.3, all(...line.map((chip, index) => all(chip.position(new Vector2(0, -180 + index * 92), 0.9, moveEasing), chip.recolor(palette.edge))))),
    delay(1.6, all(line[2].recolor('pink'), nudge(line[2], 1.1))),
    delay(disposeDuration * 0.6, all(discarded.opacity(0, 0.9), ...line.map(chip => all(chip.opacity(0, 0.9), chip.scale(0.85, 0.9, moveEasing))))),
    delay(disposeDuration * 0.6 + 0.6, nudge(untouched, 1.05)),
  );
  line.forEach(chip => chip.remove());
  allOrNothing.remove();

  const endpoint = new Pill({text: 'POST /brew-async/brew/Ristretto  400', code: true, color: 'blue', size: 26, x: 0, y: -330, opacity: 0});
  const exception = new Pill({text: 'ValidationException', color: 'pink', size: 24, x: 0, y: 0, opacity: 0});
  camera.add(endpoint);
  camera.add(exception);
  const exceptionDuration = narrator.duration('tx-exception');
  yield* narrator.beat('tx-exception',
    arrive(endpoint),
    delay(0.5, all(exception.opacity(1, 0.3), exception.y(-220, 1.2, moveEasing))),
    delay(1.8, nudge(endpoint, 1.06)),
    camera.focusOnPoint(new Vector2(0, -160), {zoom: 1.1, duration: exceptionDuration - 0.2}),
  );
  discarded.remove();

  // Live: the transactional machine never moved, and brews the next espresso.
  const rightDuration = narrator.duration('tx-live-right');
  yield* narrator.beat('tx-live-right',
    camera.reset(1),
    all(leave(endpoint, 0, -40, 0.6), leave(exception, 0, -40, 0.6), leave(untouched, 0, 40, 0.6)),
    delay(0.3, all(machines.opacity(1, 0.6, enterEasing), machines.scale(1, 0.7, moveEasing))),
    machines.play(rightDuration, {from: right - 1.5, to: espresso - 0.8}),
    delay(1, camera.focusOn(() => machines.pagePoint(machines.right, 400, 380), {zoom: 1.28, duration: rightDuration - 1.2, clear: machines.frames})),
  );
  endpoint.remove();
  exception.remove();
  untouched.remove();
  const espressoDuration = narrator.duration('tx-live-espresso');
  yield* narrator.beat('tx-live-espresso',
    machines.play(espressoDuration, {from: espresso - 0.8, to: pressure + 0.4}),
    camera.reset(1.4),
  );
  const brewTag = new Pill({text: 'Brew', code: true, color: 'pink', size: 28, x: -455, y: 470, opacity: 0});
  const asyncTag = new Pill({text: 'BrewAsync', code: true, color: 'green', size: 28, x: 455, y: 470, opacity: 0});
  camera.add(brewTag);
  camera.add(asyncTag);
  const differenceDuration = narrator.duration('tx-difference');
  yield* narrator.beat('tx-difference',
    machines.play(differenceDuration, {from: pressure + 0.4}),
    all(machines.y(-40, 1, moveEasing), machines.scale(0.92, 1, moveEasing)),
    delay(0.8, all(arrive(brewTag), delay(0.3, arrive(asyncTag)))),
    delay(1.2, camera.focusOnPoint(new Vector2(-200, 0), {zoom: 1.04, duration: (differenceDuration - 1.4) / 2})),
    delay(1.2 + (differenceDuration - 1.4) / 2, camera.focusOnPoint(new Vector2(200, 0), {zoom: 1.04, duration: (differenceDuration - 1.4) / 2})),
  );

  // Rollback mode reverts applied writes when a write fails during the commit itself.
  const timeline = writeChips();
  timeline.forEach((chip, index) => {
    chip.position(new Vector2(-560 + index * 380, 120));
    chip.dot!.fill(palette.green);
    camera.add(chip);
  });
  const rollbackDuration = narrator.duration('tx-rollback');
  yield* narrator.beat('tx-rollback',
    camera.reset(1),
    all(leave(machines, 0, -100, 0.7), leave(brewTag, 0, 60, 0.6), leave(asyncTag, 0, 60, 0.6)),
    delay(0.5, sequence(0.2, ...timeline.map(chip => arrive(chip)))),
    delay(2, all(timeline[3].recolor('pink'), nudge(timeline[3], 1.1))),
    delay(2.8, sequence(0.5, ...[2, 1, 0].map(index => all(timeline[index].recolor(palette.edge), timeline[index].y(180, 0.5, moveEasing))))),
    delay(1, camera.focusOnPoint(new Vector2(0, 140), {zoom: 1.08, duration: rollbackDuration - 1.2})),
  );
  machines.remove();
  brewTag.remove();
  asyncTag.remove();
  const rollback = new Pill({text: 'Rollback', code: true, color: 'green', size: 30, x: -230, y: -100, opacity: 0});
  const bestEffort = new Pill({text: 'BestEffort', code: true, color: 'blue', size: 30, x: 230, y: -100, opacity: 0});
  camera.add(rollback);
  camera.add(bestEffort);
  const modesDuration = narrator.duration('tx-modes');
  yield* narrator.beat('tx-modes',
    arrive(rollback),
    delay(0.4, arrive(bestEffort)),
    delay(1.4, all(nudge(rollback, 1.1), ...timeline.slice(0, 3).map(chip => chip.y(120, 0.5, moveEasing)))),
    delay(modesDuration * 0.62, all(bestEffort.opacity(1, 0.3), nudge(bestEffort, 1.08), rollback.opacity(0.55, 0.4), ...timeline.slice(0, 3).map(chip => chip.recolor('green')))),
    camera.focusOnPoint(new Vector2(0, 20), {zoom: 1.1, duration: modesDuration - 0.2}),
  );

  // Transactions on one context run one at a time.
  const first = new Box({label: 'first brew', width: 460, height: 260, x: -300, y: 20, opacity: 0});
  const secondBrew = new Box({label: 'second brew', width: 460, height: 260, x: 300, y: 20, opacity: 0});
  const firstPending = new Pill({text: '4 writes', code: true, color: 'green', size: 26, x: -300, y: 20, opacity: 0});
  const waits = new Pill({text: 'waits', color: 'orange', size: 26, x: 300, y: 20, opacity: 0});
  [first, secondBrew, firstPending, waits].forEach(node => camera.add(node));
  const lockDuration = narrator.duration('tx-lock');
  yield* narrator.beat('tx-lock',
    camera.reset(1),
    all(...[...timeline, rollback, bestEffort].map(node => leave(node, 0, -60, 0.6))),
    delay(0.5, all(arrive(first), arrive(firstPending), delay(0.3, all(arrive(secondBrew), arrive(waits))))),
    delay(lockDuration * 0.45, all(first.opacity(0, 0.6), firstPending.opacity(0, 0.6), firstPending.y(-60, 0.6, moveEasing))),
    delay(lockDuration * 0.45 + 0.6, all(secondBrew.x(-300, 0.9, moveEasing), waits.x(-300, 0.9, moveEasing))),
    delay(lockDuration * 0.45 + 1.6, chain(waits.retext('machine busy'), waits.recolor('pink'))),
    camera.focusOnPoint(new Vector2(0, 20), {zoom: 1.08, duration: lockDuration - 0.2}),
  );
  [...timeline, rollback, bestEffort, first, firstPending].forEach(node => node.remove());

  // One context, one async flow, no nesting.
  const ring = new Box({label: 'one context, one async flow', width: 760, height: 420, x: -300, y: 20, color: palette.blue, opacity: 0});
  const nested = new Box({label: 'nested', width: 280, height: 150, x: 520, y: 20, color: palette.pink, opacity: 0});
  camera.add(ring);
  camera.add(nested);
  const limitsDuration = narrator.duration('tx-limits');
  yield* narrator.beat('tx-limits',
    all(leave(waits, 0, 0, 0.4), leave(secondBrew, 0, 0, 0.4)),
    delay(0.3, arrive(ring, 0.96)),
    delay(limitsDuration * 0.45, chain(
      all(nested.opacity(1, 0.3), nested.x(-120, 0.8, moveEasing)),
      all(nested.x(380, 0.6, moveEasing), nested.frame.fill('rgba(255, 55, 95, 0.25)', 0.2).to('rgba(44, 44, 46, 0.55)', 0.4)),
    )),
    camera.focusOnPoint(new Vector2(0, 20), {zoom: 1.04, duration: limitsDuration - 0.2}),
  );
});
