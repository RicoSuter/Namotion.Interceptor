import {makeScene2D} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2} from '@revideo/core';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {enterEasing, moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {channels, dependencies, recipeTree} from '../diagrams/flows';
import {arrive, Box, leave, nudge, Pill} from './shared';

export default makeScene2D('recap', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Recap', kicker: 'What you can do now', colors: ['green', 'cyan', 'blue']});
  view.add(title);
  yield* narrator.beat('recap-title', title.enter());

  // Derived properties.
  const graph = new FlowDiagram({definition: dependencies, scale: 0.9, y: 20});
  camera.add(graph);
  yield* graph.build();
  const derivedDuration = narrator.duration('recap-derived');
  yield* narrator.beat('recap-derived',
    title.exit(),
    delay(0.3, all(graph.reveal(0), graph.reveal(1), graph.reveal(2))),
    delay(1.6, chain(
      graph.pulse('temperature', 'hot', 0.6),
      all(graph.pulse('hot', 'ready', 0.6), graph.pulse('hot', 'status', 0.6), graph.pulse('temperature', 'status', 0.8)),
    )),
    delay(1, camera.focusOnPoint(new Vector2(0, 20), {zoom: 1.06, duration: derivedDuration - 1.2})),
  );

  // Change streams.
  const fanOut = new FlowDiagram({definition: channels});
  camera.add(fanOut);
  yield* fanOut.build();
  const streamsDuration = narrator.duration('recap-streams');
  yield* narrator.beat('recap-streams',
    camera.reset(0.8),
    all(graph.opacity(0, 0.6, moveEasing), graph.scale(0.8, 0.7, moveEasing)),
    delay(0.4, all(fanOut.reveal(0), delay(0.3, fanOut.reveal(1)))),
    delay(1.4, sequence(0.4, fanOut.pulse('write', 'observable', 0.7), fanOut.pulse('write', 'property', 0.7), fanOut.pulse('write', 'queue', 0.7))),
    delay(1, camera.focusOnPoint(new Vector2(100, 0), {zoom: 1.05, duration: streamsDuration - 1.2})),
  );
  graph.remove();

  // Lifecycle.
  const tree = new FlowDiagram({definition: recipeTree, y: 20});
  camera.add(tree);
  yield* tree.build();
  const attached = new Pill({text: 'attached', color: 'green', size: 24, opacity: 0});
  camera.add(attached);
  const lifecycleDuration = narrator.duration('recap-lifecycle');
  yield* narrator.beat('recap-lifecycle',
    camera.reset(0.8),
    all(fanOut.opacity(0, 0.6, moveEasing), fanOut.scale(0.8, 0.7, moveEasing)),
    delay(0.4, tree.reveal(0)),
    delay(1.4, tree.reveal(1)),
    delay(2, (function* () {
      attached.position(tree.node('ristretto').position().add([250, 20]));
      yield* arrive(attached);
    })()),
    delay(1, camera.focusOnPoint(new Vector2(150, 140), {zoom: 1.08, duration: lifecycleDuration - 1.2, clear: tree.boxes})),
  );
  fanOut.remove();

  // Transactions.
  const pending = new Box({label: 'one transaction', width: 1500, height: 200, y: -20, color: palette.green, opacity: 0});
  camera.add(pending);
  const names = ['State', 'ActiveRecipeName', 'TargetTemperature', 'IsRunning'];
  const chips = names.map((name, index) => {
    const chip = new Pill({text: name, code: true, color: 'green', size: 26, x: -540 + index * 360, y: -20, opacity: 0});
    chip.dot!.fill(palette.edge);
    camera.add(chip);
    return chip;
  });
  const allOrNothing = new Pill({text: 'all or nothing', color: 'green', size: 32, y: 200, opacity: 0});
  camera.add(allOrNothing);
  const transactionsDuration = narrator.duration('recap-transactions');
  yield* narrator.beat('recap-transactions',
    camera.reset(0.8),
    all(leave(tree, 0, 60, 0.6), leave(attached, 0, 60, 0.6)),
    delay(0.4, arrive(pending, 0.96)),
    delay(0.8, sequence(0.15, ...chips.map(chip => all(chip.opacity(1, 0.3, enterEasing), nudge(chip, 1.05))))),
    delay(2.2, all(...chips.map(chip => chip.recolor('green')), nudge(pending.frame, 1.03))),
    delay(2.8, arrive(allOrNothing)),
  );
  tree.remove();
  attached.remove();

  const closing = new ChapterCard({title: 'Ready to track', kicker: 'Happy brewing', colors: ['green', 'cyan', 'blue']});
  view.add(closing);
  yield* narrator.beat('recap-pointers',
    closing.enter(),
  );
});
