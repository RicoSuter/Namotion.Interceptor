import {makeScene2D, Rect} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2} from '@revideo/core';
import boilerSource from '../../../domain/Coffee/Boiler.cs?raw';
import machineSource from '../../../domain/Coffee/CoffeeMachine.cs?raw';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette, type AccentColor} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {enterEasing, moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {dependencies} from '../diagrams/flows';
import {arrive, leave, machineAndStream, nudge, Pill, toLocal} from './shared';

/** Code card geometry at 26 px, for underlining words: JetBrains Mono advances 0.6 em per character. */
const codeWidth = 1560;
const codeHeight = 660;
const fontSize = 26;
const characterWidth = fontSize * 0.6;
const rowHeight = Math.round(fontSize * 1.5);
const viewportTop = -codeHeight / 2 + 76 + 20;

export default makeScene2D('derived', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Derived properties', kicker: 'Chapter 2'});
  view.add(title);
  yield* narrator.beat('derived-title', title.enter());

  const derivedCode = extractRegion(machineSource, 'Derived');
  const code = new CodeCard({fileName: 'CoffeeMachine.cs', width: codeWidth, height: codeHeight, codeFontSize: fontSize, opacity: 0, scale: 0.94});
  camera.add(code);
  const lineFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => code.linesCenter(from, to).add(code.absolutePosition()).scale(0.5), {zoom, duration, clear: code});
  yield* narrator.beat('derived-code',
    title.exit(),
    delay(0.3, arrive(code, 0.94)),
    delay(0.6, code.show(derivedCode, 2.4)),
    delay(3.2, code.focus(0, 0)),
    delay(4.4, code.focus(3, 3)),
  );

  // IsReady reads State, Boiler.IsHot and WaterTank.IsLow.
  const underline = (line: number, column: number, length: number, color: AccentColor) => {
    const marker = (
      <Rect x={-codeWidth / 2 + 40 + (column + length / 2) * characterWidth} y={viewportTop + (line + 1) * rowHeight - 4}
        width={length * characterWidth} height={5} radius={3} fill={palette[color]} opacity={0} scale={[0, 1]} />
    ) as Rect;
    code.add(marker);
    return marker;
  };
  const reads = [underline(1, 23, 5, 'purple'), underline(1, 59, 12, 'pink'), underline(1, 76, 15, 'cyan')];
  const isReadyDuration = narrator.duration('derived-isready');
  yield* narrator.beat('derived-isready',
    code.focus(1, 1),
    lineFocus(1, 1, 1.12, 1.4),
    delay(1.6, sequence(Math.min(0.9, (isReadyDuration - 2.6) / 3), ...reads.map(marker => all(marker.opacity(1, 0.3), marker.scale([1, 1], 0.5, enterEasing))))),
    delay(1.8, camera.focusOnPoint(new Vector2(350, -150), {zoom: 1.2, duration: isReadyDuration - 2})),
  );
  yield* narrator.beat('derived-status',
    camera.reset(1),
    all(...reads.map(marker => marker.opacity(0, 0.4))),
    delay(0.3, code.focus(4, 12)),
    delay(1.6, code.focus(10, 10)),
    delay(3.2, code.focus(6, 6)),
    delay(1.4, lineFocus(8, 8, 1.06, narrator.duration('derived-status') - 1.6)),
  );
  reads.forEach(marker => marker.remove());
  yield* narrator.beat('derived-no-events',
    camera.reset(1.2),
    code.focus(0, 12),
    delay(1.4, chain(code.focus(1, 1, 0.4), nudge(code, 1.02), code.unfocus(0.6))),
  );

  // The reads become dependencies.
  const graph = new FlowDiagram({definition: dependencies, x: 470, y: 30, scale: 0.78});
  camera.add(graph);
  yield* graph.build();
  yield* narrator.beat('derived-record',
    all(code.x(-520, 1, moveEasing), code.scale(0.54, 1, moveEasing)),
    delay(0.8, graph.reveal(0)),
    delay(2.4, sequence(0.3, graph.pulse('state', 'ready', 0.8), graph.pulse('hot', 'ready', 0.8), graph.pulse('low', 'ready', 0.8))),
  );
  yield* narrator.beat('derived-chain',
    code.morph(extractRegion(boilerSource, 'IsHot'), 1.2, 'Boiler.cs'),
    delay(0.4, code.focus(1, 1)),
    delay(1.6, graph.reveal(1)),
    delay(3.2, all(graph.pulse('temperature', 'hot', 0.8), graph.pulse('target', 'hot', 0.8), graph.pulse('level', 'low', 0.8))),
    delay(2.6, nudge(graph.node('hot'), 1.06)),
  );

  // Evaluated once on attach, so the dependencies are known from the start.
  const initialDuration = narrator.duration('derived-initial');
  yield* narrator.beat('derived-initial',
    camera.reset(1),
    leave(code, -200, 0, 0.8),
    all(graph.x(0, 1.2, moveEasing), graph.scale(0.92, 1.2, moveEasing)),
    delay(1.4, chain(
      all(graph.pulse('temperature', 'hot', 0.7), graph.pulse('target', 'hot', 0.7), graph.pulse('level', 'low', 0.7)),
      all(graph.pulse('state', 'ready', 0.7), graph.pulse('hot', 'ready', 0.7), graph.pulse('low', 'ready', 0.7)),
    )),
    delay(1.4, camera.focusOnPoint(new Vector2(0, 30), {zoom: 1.05, duration: initialDuration - 1.6})),
  );

  // A temperature write ripples through IsHot into IsReady and Status.
  const node = (id: string) => toLocal(camera, graph.node(id).absolutePosition());
  const write = new Pill({text: 'Temperature = 78.4', code: true, color: 'pink', size: 24, opacity: 0});
  camera.add(write);
  const rippleDuration = narrator.duration('derived-ripple');
  yield* narrator.beat('derived-ripple',
    graph.reveal(2),
    (function* () {
      const temperature = node('temperature');
      write.position(temperature.add([-420, 60]));
      yield* delay(1.2, all(arrive(write), write.position(temperature.add([-340, 0]), 0.8, moveEasing)));
    })(),
    delay(2.4, chain(
      graph.pulse('temperature', 'hot', 0.6),
      all(graph.pulse('hot', 'ready', 0.6), graph.pulse('hot', 'status', 0.6), graph.pulse('temperature', 'status', 0.8)),
    )),
    delay(1, camera.focusOnPoint(new Vector2(0, 30), {zoom: 1.08, duration: rippleDuration - 1.2})),
  );

  // Only a changed value is published.
  const statusValue = new Pill({text: '"Heating 78 °C"', code: true, color: 'purple', size: 24, opacity: 0});
  const readyValue = new Pill({text: 'false, no change', code: true, color: 'purple', size: 24, opacity: 0});
  camera.add(statusValue);
  camera.add(readyValue);
  const compareDuration = narrator.duration('derived-compare');
  yield* narrator.beat('derived-compare',
    (function* () {
      statusValue.position(node('status').add([0, 100]));
      readyValue.position(node('ready').add([0, -100]));
      yield* all(arrive(statusValue), delay(0.3, arrive(readyValue)));
    })(),
    delay(1.6, chain(statusValue.retext('"Heating 79 °C"'), nudge(statusValue, 1.08))),
    delay(2.6, all(readyValue.opacity(0.55, 0.6), nudge(graph.node('ready'), 1.03))),
    camera.focusOnPoint(new Vector2(250, 30), {zoom: 1.08, duration: compareDuration - 0.2}),
  );
  const acrossDuration = narrator.duration('derived-across');
  yield* narrator.beat('derived-across',
    all(leave(write, 0, -20, 0.5), leave(statusValue, 0, 20, 0.5), leave(readyValue, 0, -20, 0.5)),
    camera.focusOnPoint(new Vector2(-150, -90), {zoom: 1.12, duration: 1.6}),
    delay(1.6, camera.focusOnPoint(new Vector2(-150, 170), {zoom: 1.12, duration: acrossDuration - 1.8})),
    delay(0.8, chain(nudge(graph.node('hot'), 1.08), graph.pulse('hot', 'ready', 0.7))),
    delay(2.6, chain(nudge(graph.node('low'), 1.08), graph.pulse('low', 'ready', 0.7))),
  );
  write.remove();
  statusValue.remove();
  readyValue.remove();

  // Dependencies are recorded on every evaluation: while brewing, Status reads only State and the recipe name.
  const branch = new CodeCard({fileName: 'CoffeeMachine.cs', width: codeWidth, height: codeHeight, codeFontSize: fontSize, code: derivedCode, opacity: 0, scale: 0.94});
  camera.add(branch);
  const branchFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => branch.linesCenter(from, to).add(branch.absolutePosition()).scale(0.5), {zoom, duration, clear: branch});
  yield* narrator.beat('derived-branch',
    camera.reset(1),
    all(graph.opacity(0, 0.7, moveEasing), graph.scale(0.8, 0.8, moveEasing)),
    delay(0.4, arrive(branch, 0.94)),
    delay(1.2, branch.focus(4, 12)),
    delay(2.6, branch.focus(6, 6)),
    delay(2.8, branchFocus(6, 6, 1.15, narrator.duration('derived-branch') - 3)),
  );
  graph.remove();
  code.remove();

  // Concurrent writers: derived values settle on the getter's result.
  const writers = (['Simulator', 'Request', 'Background job'] as const).map((text, index) =>
    new Pill({text, color: (['orange', 'blue', 'cyan'] as AccentColor[])[index], size: 24, x: -900, y: 300 + index * 70, opacity: 0}));
  writers.forEach(writer => camera.add(writer));
  const settles = new Pill({text: 'settles on the getter result', color: 'green', size: 26, x: 0, y: 420, opacity: 0});
  camera.add(settles);
  const settleDuration = narrator.duration('derived-settle');
  yield* narrator.beat('derived-settle',
    camera.reset(1),
    all(branch.y(-120, 1, moveEasing), branch.scale(0.8, 1, moveEasing)),
    delay(0.6, all(...writers.map((writer, index) => delay(index * 0.15, all(writer.opacity(1, 0.3), writer.x(-480 + index * 40, 1.2, moveEasing), writer.y(220 + index * 40, 1.2, moveEasing)))))),
    delay(2.2, all(...writers.map(writer => all(writer.opacity(0, 0.5), writer.scale(0.8, 0.5, moveEasing))))),
    delay(2.6, arrive(settles)),
    delay(2.6, camera.focusOnPoint(new Vector2(0, 100), {zoom: 1.06, duration: settleDuration - 2.8})),
  );
  writers.forEach(writer => writer.remove());

  // The live sample heating up.
  const heat = machineAndStream('heat');
  camera.add(heat);
  yield heat.left;
  yield heat.right;
  const warm = heat.mark('warm');
  const ready = heat.mark('ready');
  const liveDuration = narrator.duration('derived-live');
  yield* narrator.beat('derived-live',
    camera.reset(1),
    all(leave(branch, 0, -120, 0.8), leave(settles, 0, 60, 0.6)),
    delay(0.4, heat.arrive()),
    heat.play(liveDuration, {from: heat.mark('open'), to: warm - 4}),
    delay(1.6, camera.focusOn(() => heat.pagePoint(heat.left, 400, 142), {zoom: 1.3, duration: liveDuration - 1.8, clear: heat.frames})),
  );
  branch.remove();
  settles.remove();

  const counter = (x: number) => heat.pagePoint(heat.right, x, 138);
  const callouts = [
    new Pill({text: '10 per second', color: 'pink', size: 24, opacity: 0}),
    new Pill({text: 'once per degree', color: 'purple', size: 24, opacity: 0}),
    new Pill({text: 'not yet', color: 'purple', size: 24, opacity: 0}),
  ];
  callouts.forEach(callout => camera.add(callout));
  const countsDuration = narrator.duration('derived-counts');
  yield* narrator.beat('derived-counts',
    heat.play(countsDuration, {from: warm - 4, to: ready - 7}),
    camera.focusOn(() => counter(400), {zoom: 1.35, duration: 1.4, clear: heat.frames}),
    delay(1.6, sequence(Math.max((countsDuration - 2.8) / 3, 0.6), ...callouts.map((callout, index) => (function* () {
      callout.position(toLocal(camera, heat.pagePoint(heat.right, [160, 399, 638][index], 0)).add([0, -96]));
      yield* arrive(callout);
    })()))),
  );
  const equal = new Pill({text: 'equal value, no change', color: 'green', size: 26, opacity: 0});
  camera.add(equal);
  const equalityDuration = narrator.duration('derived-equality');
  yield* narrator.beat('derived-equality',
    heat.play(equalityDuration, {from: ready - 7, to: ready - 2.5}),
    all(...callouts.map(callout => leave(callout, 0, 20, 0.5))),
    camera.focusOn(() => heat.pagePoint(heat.right, 400, 420), {zoom: 1.12, duration: equalityDuration - 0.2, clear: heat.frames}),
    delay(1.2, (function* () {
      equal.position(toLocal(camera, heat.pagePoint(heat.right, 400, 735)));
      yield* arrive(equal);
    })()),
  );
  callouts.forEach(callout => callout.remove());
  const readyDuration = narrator.duration('derived-ready');
  yield* narrator.beat('derived-ready',
    heat.play(readyDuration, {from: ready - 2.5}),
    leave(equal, 0, 20, 0.5),
    camera.focusOn(() => heat.pagePoint(heat.right, 560, 200), {zoom: 1.35, duration: 1.6, clear: heat.frames}),
    delay(readyDuration - 2.4, camera.focusOn(() => heat.pagePoint(heat.left, 400, 142), {zoom: 1.2, duration: 2.2, clear: heat.frames})),
  );
  equal.remove();
});
