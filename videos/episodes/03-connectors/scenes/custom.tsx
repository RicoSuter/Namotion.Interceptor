import {Circle, makeScene2D} from '@revideo/2d';
import {all, chain, delay, linear, sequence, Vector2} from '@revideo/core';
import extensionsSource from '../sample/Server/Grinder/GrinderSourceExtensions.cs?raw';
import grinderSource from '../sample/Server/Grinder/GrinderSource.cs?raw';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {enterEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {grinder, twoConnectors} from '../diagrams/flows';
import {arrive, leave, Pill} from './shared';

/** The code card sits a little low so the hook names fit above it. */
const codeY = 20;

export default makeScene2D('custom', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Your own source', kicker: 'Chapter 5'});
  view.add(title);
  yield* narrator.beat('custom-title', title.enter());

  // A grinder that owns its grind size.
  const scenario = new FlowDiagram({definition: grinder});
  camera.add(scenario);
  yield* scenario.build();
  yield* narrator.beat('custom-scenario',
    title.exit(),
    delay(0.4, chain(scenario.reveal(0), scenario.reveal(1), scenario.reveal(2))),
    delay(1.2, camera.focusOnPoint(new Vector2(-100, 0), {zoom: 1.08, duration: narrator.duration('custom-scenario') - 1.4})),
  );
  yield* narrator.beat('custom-scenario-flow',
    camera.reset(1.2),
    chain(scenario.pulse('device', 'source', 0.6), scenario.pulse('source', 'hopper', 0.6)),
    delay(1.6, scenario.reveal(3)),
    delay(2.6, chain(scenario.pulse('hopper', 'source', 0.6), scenario.pulse('source', 'device', 0.6))),
  );

  // The source class and its three hooks.
  const code = new CodeCard({fileName: 'Grinder/GrinderSource.cs', width: 1500, height: 760, codeFontSize: 26, y: codeY, opacity: 0, scale: 0.94});
  camera.add(code);
  const lineFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => code.linesCenter(from, to).add(code.absolutePosition()).scale(0.5), {zoom, duration});
  yield* narrator.beat('custom-class',
    leave(scenario, 0, -120, 0.7),
    delay(0.4, arrive(code, 0.94)),
    delay(0.7, code.show(extractRegion(grinderSource, 'GrinderSourceClass'), 2.4)),
    delay(3.6, code.focus(0, 0)),
    delay(3.8, lineFocus(0, 0, 1.15, narrator.duration('custom-class') - 4)),
  );
  scenario.remove();

  // The base class restarts a failed attempt.
  const retryIcon = new Circle({size: 84, lineWidth: 7, stroke: palette.blue, startAngle: -70, endAngle: 230, endArrow: true, arrowSize: 14, lineCap: 'round', x: 560, y: codeY - 300, opacity: 0});
  const retryLabel = new Pill({text: 'retry after 10 s', color: 'blue', size: 24, x: 560, y: codeY - 200, opacity: 0});
  camera.add(retryIcon);
  camera.add(retryLabel);
  const retryDuration = narrator.duration('custom-retry-loop');
  yield* narrator.beat('custom-retry-loop',
    all(retryIcon.opacity(1, 0.4, enterEasing), retryIcon.rotation(720, retryDuration - 0.2, linear)),
    delay(0.5, arrive(retryLabel)),
    lineFocus(0, 0, 1.08, retryDuration - 0.2),
  );

  const hookNames = ['StartListeningAsync', 'LoadInitialStateAsync', 'WriteChangesAsync'];
  const hooks = hookNames.map((name, index) => new Pill({text: name, code: true, size: 24, x: (index - 1) * 480, y: codeY - 460, opacity: 0}));
  hooks.forEach(hook => camera.add(hook));
  const activate = (active: number) => all(...hooks.map((hook, index) => hook.opacity(index === active ? 1 : 0.45, 0.4)));
  yield* narrator.beat('custom-hooks',
    camera.reset(1),
    all(retryIcon.opacity(0, 0.4), leave(retryLabel, 0, -40, 0.4)),
    delay(0.3, sequence(0.3, ...hooks.map(hook => arrive(hook)))),
    delay(1.6, code.morph(extractRegion(grinderSource, 'StartListening'), 1.6)),
    delay(3.4, activate(0)),
  );
  retryIcon.remove();
  retryLabel.remove();
  // StartListening lines: 3 to 7 claim, 9 connects, 10 to 16 the loop with 14 writing and 16 disconnecting.
  yield* narrator.beat('custom-listen',
    code.focus(3, 9),
    lineFocus(3, 9, 1.1, 2),
    delay(2.8, code.focus(4, 7)),
    delay(2.8, lineFocus(4, 7, 1.18, narrator.duration('custom-listen') - 3)),
  );
  yield* narrator.beat('custom-listen-release',
    code.focus(4, 4),
    lineFocus(4, 4, 1.06, narrator.duration('custom-listen-release') - 0.2),
  );
  const loopDuration = narrator.duration('custom-listen-loop');
  yield* narrator.beat('custom-listen-loop',
    code.focus(9, 9),
    lineFocus(9, 9, 1.1, 1.4),
    delay(1.6, code.focus(10, 16)),
    delay(1.6, lineFocus(10, 16, 1.06, 1.6)),
    delay(3.4, code.focus(14, 14)),
    delay(3.4, lineFocus(14, 14, 1.15, loopDuration - 5.8)),
    delay(loopDuration - 2.2, code.focus(16, 16)),
    delay(loopDuration - 2.2, lineFocus(16, 16, 1.15, 2)),
  );
  yield* narrator.beat('custom-load',
    camera.reset(1),
    delay(0.2, code.morph(extractRegion(grinderSource, 'LoadInitialState'), 1.4)),
    delay(0.4, activate(1)),
    delay(2.2, code.focus(3, 4)),
    delay(4.4, code.focus(7, 8)),
    delay(2.4, lineFocus(4, 7, 1.12, narrator.duration('custom-load') - 2.6)),
  );
  yield* narrator.beat('custom-write',
    camera.reset(1),
    delay(0.2, code.morph(extractRegion(grinderSource, 'WriteChanges'), 1.4)),
    delay(0.4, activate(2)),
    delay(2.2, code.focus(6, 10)),
    delay(2.4, lineFocus(6, 10, 1.12, narrator.duration('custom-write') - 2.6)),
  );
  yield* narrator.beat('custom-write-failure',
    code.focus(15, 15),
    lineFocus(15, 15, 1.15, 2.2),
    delay(3.2, code.scale(1.02, 0.3).to(1, 0.4)),
  );

  // Registration as a singleton and a hosted service.
  yield* narrator.beat('custom-register',
    camera.reset(1),
    all(...hooks.map(hook => leave(hook, 0, -40, 0.5))),
    delay(0.3, code.morph(extractRegion(extensionsSource, 'AddGrinderSource'), 1.6, 'Grinder/GrinderSourceExtensions.cs')),
    delay(2.6, code.focus(4, 7)),
    delay(2.8, lineFocus(4, 7, 1.1, 2.4)),
    delay(5.4, code.focus(8, 9)),
    delay(5.6, lineFocus(8, 9, 1.15, narrator.duration('custom-register') - 5.8)),
  );
  hooks.forEach(hook => hook.remove());

  // The server's two connectors.
  const both = new FlowDiagram({definition: twoConnectors, scale: 0.94});
  camera.add(both);
  yield* both.build();
  const pulseChain = (duration = 0.5) => chain(
    both.pulse('device', 'source', duration), both.pulse('source', 'machine', duration),
    both.pulse('machine', 'handler', duration), both.pulse('handler', 'mirror', duration),
  );
  yield* narrator.beat('custom-running',
    camera.reset(1),
    leave(code, 0, -150, 0.7),
    delay(0.5, both.reveal(0)),
    delay(2, pulseChain()),
    delay(2, camera.focusOnPoint(new Vector2(-80, 0), {zoom: 1.04, duration: narrator.duration('custom-running') - 2.2})),
  );
  code.remove();
  yield* narrator.beat('custom-simulated',
    both.retext('device', {label: 'Simulated grinder', detail: 'IGrinderDevice'}),
    camera.focusOn(both.node('device'), {zoom: 1.25, duration: 1.6}),
    delay(1.4, all(pulseChain(0.4), camera.focusOnPoint(new Vector2(0, 0), {zoom: 1.02, duration: 2.8}))),
  );
});
