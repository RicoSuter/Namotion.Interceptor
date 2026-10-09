import {makeScene2D} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2} from '@revideo/core';
import hostSource from '../sample/MachineHost.cs?raw';
import streamSource from '../sample/ChangeStream.cs?raw';
import stepsSource from '../sample/Steps.cs?raw';
import {Arrow} from '../../../theme/components/Arrow';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {Terminal} from '../../../theme/components/Terminal';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {moveEasing} from '../../../theme/style';
import {useTerminal, useTiming} from '../../../theme/variables';
import {channels} from '../diagrams/flows';
import {arrive, FieldCard, leave, machineAndStream, nudge, Pill, toLocal} from './shared';

/** Vertical center of a row in the change stream page, counted from the newest row at the top. */
const streamRowY = (index: number) => 231 + index * 88;

export default makeScene2D('streams', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Change streams', kicker: 'Chapter 3'});
  view.add(title);
  yield* narrator.beat('streams-title', title.enter());

  // What a change carries, with values from a status change of the sample.
  const change = new FieldCard({
    title: 'SubjectPropertyChange',
    fields: [
      ['Property', 'CoffeeMachine.Status'],
      ['Old value', '"Heating 91 °C"'],
      ['New value', '"Heating 92 °C"'],
      ['Timestamp', 'DateTimeOffset'],
      ['Revision', 'per subject'],
      ['Origin', 'local or a source'],
    ],
    width: 900, opacity: 0, scale: 0.94,
  });
  camera.add(change);
  const changeDuration = narrator.duration('streams-change');
  yield* narrator.beat('streams-change',
    title.exit(),
    delay(0.3, arrive(change, 0.94)),
    delay(1.2, sequence(Math.max((changeDuration - 2) / 5, 0.4), ...[0, 1, 2, 3, 4].map(index => change.reveal(index)))),
    delay(1, camera.focusOnPoint(new Vector2(0, -40), {zoom: 1.08, duration: changeDuration - 1.2, clear: change})),
  );

  // The origin keeps a source's own values from being echoed back to it.
  const connector = new Pill({text: 'Connector', color: 'orange', size: 26, x: 700, y: 230, opacity: 0});
  const echo = new Arrow({curve: {p0: {x: 470, y: 230}, p1: {x: 520, y: 230}, p2: {x: 540, y: 230}, p3: {x: 590, y: 230}}, color: palette.pink, dashed: true});
  camera.add(connector);
  camera.add(echo);
  const sourceDuration = narrator.duration('streams-source');
  yield* narrator.beat('streams-source',
    change.reveal(5),
    camera.focusOnPoint(new Vector2(180, 40), {zoom: 1.04, duration: 1.2}),
    delay(0.6, change.highlight(5)),
    delay(1.4, arrive(connector)),
    delay(2.4, echo.line.end(0.6, 0.7, moveEasing)),
    delay(3.3, echo.opacity(0, 0.6)),
    delay(sourceDuration - 1, change.highlight(-1)),
  );
  echo.remove();
  const getters = [
    new Pill({text: 'GetOldValue<T>()', code: true, size: 24, opacity: 0}),
    new Pill({text: 'GetNewValue<T>()', code: true, size: 24, opacity: 0}),
  ];
  getters.forEach(getter => camera.add(getter));
  const valuesDuration = narrator.duration('streams-values');
  yield* narrator.beat('streams-values',
    leave(connector, 40, 0, 0.5),
    change.highlight(1),
    (function* () {
      getters.forEach((getter, index) => getter.position(toLocal(camera, change.row(index + 1).absolutePosition()).add([660, 0])));
      yield* all(arrive(getters[0]), delay(valuesDuration * 0.45, all(change.highlight(2), arrive(getters[1]))));
    })(),
    camera.focusOnPoint(new Vector2(220, -100), {zoom: 1.12, duration: valuesDuration - 0.2}),
  );
  connector.remove();

  // One write, three channels.
  const fanOut = new FlowDiagram({definition: channels});
  camera.add(fanOut);
  yield* fanOut.build();
  const channelsDuration = narrator.duration('streams-channels');
  yield* narrator.beat('streams-channels',
    camera.reset(1),
    all(leave(change, -200, 0, 0.8), ...getters.map(getter => leave(getter, -200, 0, 0.8))),
    delay(0.5, fanOut.reveal(0)),
    delay(1.4, fanOut.reveal(1)),
    delay(2.6, sequence(0.5, fanOut.pulse('write', 'observable', 0.8), fanOut.pulse('write', 'queue', 0.8), fanOut.pulse('write', 'property', 0.8))),
    delay(2.4, camera.focusOnPoint(new Vector2(120, 0), {zoom: 1.06, duration: channelsDuration - 2.6})),
  );
  change.remove();
  getters.forEach(getter => getter.remove());

  // The observable, as the sample's change stream uses it.
  const code = new CodeCard({fileName: 'ChangeStream.cs', width: 1560, height: 620, codeFontSize: 28, opacity: 0, scale: 0.94});
  camera.add(code);
  const lineFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => code.linesCenter(from, to).add(code.absolutePosition()).scale(0.5), {zoom, duration, clear: code});
  yield* narrator.beat('streams-observable',
    camera.reset(1),
    leave(fanOut, 0, -100, 0.7),
    delay(0.4, arrive(code, 0.94)),
    delay(0.7, code.show(extractRegion(streamSource, 'Observable'), 2.2)),
    delay(3.1, code.focus(0, 0)),
    delay(3.3, lineFocus(0, 0, 1.15, narrator.duration('streams-observable') - 3.5)),
  );
  fanOut.remove();
  const physics = new Pill({text: 'skips the physics', color: 'orange', size: 24, opacity: 0});
  camera.add(physics);
  const rxDuration = narrator.duration('streams-rx');
  yield* narrator.beat('streams-rx',
    code.focus(4, 5),
    lineFocus(4, 5, 1.12, 1.6),
    delay(rxDuration * 0.55, all(code.focus(5, 5), (function* () {
      physics.position(toLocal(camera, code.linesCenter(5, 5)).add([560, 0]));
      yield* arrive(physics);
    })())),
    delay(rxDuration * 0.55, lineFocus(5, 5, 1.2, rxDuration * 0.45 - 0.2)),
  );
  const ui = new Pill({text: 'user interfaces, queries', color: 'blue', size: 24, opacity: 0});
  camera.add(ui);
  yield* narrator.beat('streams-ui',
    leave(physics, 30, 0, 0.5),
    code.focus(6, 10),
    lineFocus(6, 10, 1.06, narrator.duration('streams-ui') - 0.2),
    delay(0.8, (function* () {
      ui.position(toLocal(camera, code.linesCenter(7, 7)).add([480, 0]));
      yield* arrive(ui);
    })()),
  );
  physics.remove();
  const dispose = new Pill({text: 'Dispose() to stop', code: true, color: 'pink', size: 24, opacity: 0});
  camera.add(dispose);
  yield* narrator.beat('streams-dispose',
    leave(ui, 30, 0, 0.5),
    code.focus(2, 4),
    camera.reset(1.2),
    delay(1.2, (function* () {
      dispose.position(toLocal(camera, code.linesCenter(2, 2)).add([400, 0]));
      yield* arrive(dispose);
    })()),
  );
  ui.remove();

  // A brew in the live sample: six changes in write order.
  const brew = machineAndStream('brew');
  camera.add(brew);
  yield brew.left;
  yield brew.right;
  const click = brew.mark('click');
  const rows = brew.mark('rows');
  const pressure = brew.mark('pressure');
  const done = brew.mark('done');
  const clickDuration = narrator.duration('streams-brew-click');
  yield* narrator.beat('streams-brew-click',
    camera.reset(1),
    all(leave(code, 0, -120, 0.7), leave(dispose, 0, -120, 0.7)),
    delay(0.3, brew.arrive()),
    brew.play(clickDuration, {from: click - 2, to: rows}),
    delay(1, camera.focusOn(() => brew.pagePoint(brew.left, 200, 556), {zoom: 1.35, duration: clickDuration - 1.2, clear: brew.frames})),
  );
  code.remove();
  dispose.remove();
  const numbers = [6, 5, 4, 3, 2, 1].map((order, index) => new Pill({text: String(order), size: 22, fill: palette.blue, opacity: 0}));
  numbers.forEach(number => camera.add(number));
  const rowsDuration = narrator.duration('streams-brew-rows');
  yield* narrator.beat('streams-brew-rows',
    brew.play(rowsDuration, {from: rows, to: rows + 1.2}),
    camera.focusOn(() => brew.pagePoint(brew.right, 400, 440), {zoom: 1.32, duration: 1.4, clear: brew.frames}),
    delay(1.6, sequence(Math.max((rowsDuration - 2.4) / 6, 0.3), ...[5, 4, 3, 2, 1, 0].map(index => (function* () {
      numbers[index].position(toLocal(camera, brew.pagePoint(brew.right, 26, streamRowY(index))));
      yield* arrive(numbers[index], 0.6);
    })()))),
  );
  const noRecipe = new Pill({text: 'no recipe yet', color: 'pink', size: 22, opacity: 0});
  camera.add(noRecipe);
  const stepDuration = narrator.duration('streams-brew-step');
  yield* narrator.beat('streams-brew-step',
    brew.play(stepDuration, {from: rows + 1.2, to: rows + 1.2 + stepDuration * 0.5}),
    all(...numbers.map(number => number.opacity(0, 0.4))),
    camera.focusOn(() => brew.pagePoint(brew.right, 400, streamRowY(3)), {zoom: 1.35, duration: 1.4, clear: brew.frames}),
    delay(1.6, (function* () {
      noRecipe.position(toLocal(camera, brew.pagePoint(brew.right, 640, streamRowY(3) + 18)));
      yield* arrive(noRecipe);
    })()),
  );
  numbers.forEach(number => number.remove());
  const doneDuration = narrator.duration('streams-brew-done');
  yield* narrator.beat('streams-brew-done',
    brew.play(doneDuration, {from: rows + 1.2 + stepDuration * 0.5}),
    leave(noRecipe, 0, 20, 0.5),
    camera.focusOn(() => brew.pagePoint(brew.left, 400, 430), {zoom: 1.3, duration: 1.6, clear: brew.frames}),
    delay(doneDuration - (done - pressure) / 2 - 2.4, camera.focusOn(() => brew.pagePoint(brew.right, 400, 420), {zoom: 1.12, duration: 2.2, clear: brew.frames})),
  );
  noRecipe.remove();

  // One property of one subject.
  const property = new CodeCard({fileName: 'MachineHost.cs', width: 1560, height: 420, codeFontSize: 28, opacity: 0, scale: 0.94});
  camera.add(property);
  const propertyFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => property.linesCenter(from, to).add(property.absolutePosition()).scale(0.5), {zoom, duration, clear: property});
  yield* narrator.beat('streams-property',
    camera.reset(1),
    all(brew.opacity(0, 0.7, moveEasing), brew.scale(0.9, 0.8, moveEasing)),
    delay(0.4, arrive(property, 0.94)),
    delay(0.7, property.show(extractRegion(hostSource, 'ReadyLog'), 1.6)),
    delay(2.5, property.focus(0, 1)),
    delay(2.7, propertyFocus(0, 1, 1.12, narrator.duration('streams-property') - 2.9)),
  );
  brew.remove();
  const scheduled = new Pill({text: 'scheduled: after the write', color: 'green', size: 24, x: 420, y: 290, opacity: 0});
  const inline = new Pill({text: 'inline: inside the setter', color: 'orange', size: 24, x: -420, y: 290, opacity: 0});
  camera.add(scheduled);
  camera.add(inline);
  const schedulerDuration = narrator.duration('streams-scheduler');
  yield* narrator.beat('streams-scheduler',
    property.focus(4, 5),
    camera.focusOnPoint(new Vector2(0, 90), {zoom: 1.05, duration: 1.2}),
    delay(0.9, arrive(scheduled)),
    delay(schedulerDuration * 0.55, arrive(inline)),
    delay(schedulerDuration * 0.55, nudge(inline, 1.08)),
  );

  // The subscription logs when a machine becomes ready.
  const terminal = new Terminal({transcript: useTerminal('run-sample'), title: 'sample', width: 1500, opacity: 0, scale: 0.94});
  camera.add(terminal);
  const terminalDuration = narrator.duration('streams-terminal');
  yield* narrator.beat('streams-terminal',
    camera.reset(0.9),
    all(leave(property, 0, -200, 0.8), leave(scheduled, 0, 100, 0.6), leave(inline, 0, 100, 0.6)),
    delay(0.5, arrive(terminal, 0.94)),
    delay(0.8, terminal.run(terminalDuration - 1.6)),
    delay(1.8, camera.focusOnPoint(new Vector2(0, 0), {zoom: 1.1, duration: terminalDuration - 2})),
  );
  property.remove();
  scheduled.remove();
  inline.remove();

  // The queue, for one dedicated consumer thread.
  const queue = new CodeCard({fileName: 'Steps.cs', width: 1560, height: 420, codeFontSize: 28, opacity: 0, scale: 0.94, y: -60});
  camera.add(queue);
  const queueFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => queue.linesCenter(from, to).add(queue.absolutePosition()).scale(0.5), {zoom, duration, clear: queue});
  const queueDuration = narrator.duration('streams-queue');
  yield* narrator.beat('streams-queue',
    camera.reset(1),
    leave(terminal, 0, -120, 0.7),
    delay(0.4, arrive(queue, 0.94)),
    delay(0.7, queue.show(extractRegion(stepsSource, 'Queue'), 1.6)),
    delay(2.6, queue.focus(0, 0)),
    delay(4.4, queue.focus(2, 5)),
    delay(2.8, queueFocus(2, 5, 1.1, queueDuration - 3)),
  );
  terminal.remove();

  // Under concurrent writers, keep the higher revision.
  const newer = new Pill({text: 'Status, revision 42', color: 'purple', size: 26, x: 1100, y: 300, opacity: 0});
  const older = new Pill({text: 'Status, revision 41', color: 'purple', size: 26, x: 1100, y: 380, opacity: 0});
  camera.add(newer);
  camera.add(older);
  const orderDuration = narrator.duration('streams-order');
  yield* narrator.beat('streams-order',
    camera.reset(1),
    all(queue.y(-180, 1, moveEasing), queue.scale(0.8, 1, moveEasing), queue.unfocus()),
    delay(0.8, all(newer.opacity(1, 0.3), newer.x(0, 1, moveEasing))),
    delay(1.4, all(older.opacity(1, 0.3), older.x(0, 1, moveEasing))),
    delay(orderDuration * 0.55, all(newer.y(380, 0.8, moveEasing), older.y(300, 0.8, moveEasing))),
    delay(orderDuration * 0.55 + 1, chain(all(older.opacity(0.4, 0.5)), nudge(newer, 1.08))),
    delay(0.8, camera.focusOnPoint(new Vector2(0, 120), {zoom: 1.06, duration: orderDuration - 1, clear: queue})),
  );
});
