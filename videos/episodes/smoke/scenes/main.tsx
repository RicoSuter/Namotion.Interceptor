import {makeScene2D} from '@revideo/2d';
import {all, chain, delay, spring, waitFor} from '@revideo/core';
import boilerSource from '../../../domain/Coffee/Boiler.cs?raw';
import waterTankSource from '../../../domain/Coffee/WaterTank.cs?raw';
import {BrowserFrame} from '../../../theme/components/BrowserFrame';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {SequenceDiagram} from '../../../theme/components/SequenceDiagram';
import {Terminal} from '../../../theme/components/Terminal';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {extractRegion} from '../../../theme/regions';
import {arrivalSpring, enterEasing, moveEasing} from '../../../theme/style';
import {useTerminal, useTiming} from '../../../theme/variables';
import {statusFlow, updateParticipants} from '../diagrams/flow';

export default makeScene2D('main', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  // Intro
  const intro = new ChapterCard({title: 'Smoke test', kicker: 'Theme check'});
  view.add(intro);
  yield* narrator.beat('intro-title', intro.enter());

  // Code
  const code = new CodeCard({fileName: 'Boiler.cs', width: 1240, height: 780, y: -40, opacity: 0, scale: 0.94});
  camera.add(code);
  yield* narrator.beat('boiler-class',
    intro.exit(),
    delay(0.2, all(code.opacity(1, 0.5, enterEasing), spring(arrivalSpring, 0.94, 1, value => code.scale(value)))),
    delay(0.5, code.show(extractRegion(boilerSource, 'Boiler'), 1.8)),
  );
  yield* narrator.beat('boiler-derived',
    code.focus(10, 11),
    // Halfway between the card and the focused lines, so the card stays mostly in view.
    camera.focusOn(code.linesCenter(10, 11).add(code.absolutePosition()).scale(0.5), {zoom: 1.15, duration: 2.5}),
  );
  yield* narrator.beat('tank-morph',
    camera.reset(1.4),
    delay(0.4, code.morph(extractRegion(waterTankSource, 'WaterTank'), 1.6)),
    delay(3.2, code.focus(7, 8, 0.8)),
  );

  // Flow diagram
  const flow = new FlowDiagram({definition: statusFlow, y: -40});
  camera.add(flow);
  yield* flow.build();
  yield* narrator.beat('flow-build',
    all(code.opacity(0, 0.5, moveEasing), code.x(-300, 0.6, moveEasing)),
    delay(0.3, chain(flow.reveal(0), flow.reveal(1), flow.reveal(2), flow.reveal(3))),
  );
  code.remove();
  yield* narrator.beat('flow-pulse',
    chain(
      all(flow.pulse('simulator', 'boiler', 0.8), flow.pulse('simulator', 'tank', 0.8)),
      all(camera.focusOn(flow.node('machine'), {zoom: 1.18, duration: 1.4}), flow.pulse('boiler', 'machine', 1.1), flow.pulse('tank', 'machine', 1.1)),
      all(flow.pulse('machine', 'page', 0.6), camera.focusOn(flow.node('page'), {zoom: 1.1, duration: 0.9})),
    ),
  );

  // Sequence diagram
  const sequenceDiagram = new SequenceDiagram({participants: [...updateParticipants], width: 1440, height: 600, y: -60});
  yield* narrator.beat('sequence-write',
    all(camera.reset(0.9), flow.opacity(0, 0.6, moveEasing)),
    delay(0.7, chain(
      (function* () { camera.add(sequenceDiagram); yield* sequenceDiagram.appear(); })(),
      sequenceDiagram.message('simulator', 'boiler', 'Temperature = 93'),
      waitFor(0.4),
      sequenceDiagram.message('boiler', 'machine', 'IsHot changed'),
    )),
  );
  flow.remove();
  yield* narrator.beat('sequence-read',
    sequenceDiagram.message('page', 'machine', 'GET /status'),
    delay(1.8, sequenceDiagram.message('machine', 'page', 'Ready', {reply: true})),
  );

  // Live demo
  const terminal = new Terminal({transcript: useTerminal('run-sample'), title: 'sample', width: 1400, y: -60, opacity: 0, scale: 0.94});
  camera.add(terminal);
  yield* narrator.beat('live-terminal',
    all(sequenceDiagram.opacity(0, 0.5, moveEasing), sequenceDiagram.y(-80, 0.6, moveEasing)),
    delay(0.4, all(terminal.opacity(1, 0.4, enterEasing), spring(arrivalSpring, 0.94, 1, value => terminal.scale(value)))),
    delay(0.8, terminal.run(narrator.duration('live-terminal') - 0.8)),
  );
  sequenceDiagram.remove();
  const browser = new BrowserFrame({demo: 'status', address: 'localhost:5280', width: 1180, y: 480, opacity: 0});
  camera.add(browser);
  // Wait for the clip to load before the first frame that draws it.
  yield browser;
  yield* narrator.beat('live-browser',
    all(terminal.y(-600, 0.9, moveEasing), terminal.opacity(0, 0.7, moveEasing)),
    all(browser.y(-70, 0.9, moveEasing), browser.opacity(1, 0.6, enterEasing)),
    browser.play(narrator.duration('live-browser')),
    delay(1, camera.focusOn(browser, {zoom: 1.05, duration: narrator.duration('live-browser') - 1})),
  );
  terminal.remove();

  // Outro
  const outro = new ChapterCard({title: 'Ready to brew', kicker: 'That is the theme', colors: ['green', 'cyan', 'blue']});
  view.add(outro);
  yield* narrator.beat('outro-title', outro.enter(), delay(1, camera.reset(1)));
});
