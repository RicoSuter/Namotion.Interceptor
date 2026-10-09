import {makeScene2D, Txt} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2} from '@revideo/core';
import hostSource from '../sample/MachineHost.cs?raw';
import programSource from '../sample/Program.cs?raw';
import {Arrow} from '../../../theme/components/Arrow';
import {Camera} from '../../../theme/components/Camera';
import {Card} from '../../../theme/components/Card';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {enterEasing, fonts, moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {recipeTree} from '../diagrams/flows';
import {arrive, Box, leave, machineAndStream, nudge, Pill, toLocal, travel} from './shared';

export default makeScene2D('lifecycle', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Lifecycle', kicker: 'Chapter 4'});
  view.add(title);
  yield* narrator.beat('lifecycle-title', title.enter());

  // Recipes are subjects in the graph too.
  const tree = new FlowDiagram({definition: recipeTree, y: 20});
  camera.add(tree);
  yield* tree.build();
  const at = (id: string) => toLocal(camera, tree.node(id).absolutePosition());
  yield* narrator.beat('lifecycle-tree',
    title.exit(),
    delay(0.4, tree.reveal(0)),
    delay(2.2, camera.focusOnPoint(new Vector2(0, 120), {zoom: 1.12, duration: narrator.duration('lifecycle-tree') - 2.4, clear: tree.boxes})),
    delay(2.6, all(nudge(tree.node('espresso')), delay(0.3, nudge(tree.node('lungo'))))),
  );

  // The endpoint that adds the Ristretto.
  const code = new CodeCard({fileName: 'Program.cs', width: 1560, height: 700, codeFontSize: 28, opacity: 0, scale: 0.94});
  camera.add(code);
  const lineFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => code.linesCenter(from, to).add(code.absolutePosition()).scale(0.5), {zoom, duration, clear: code});
  const codeDuration = narrator.duration('lifecycle-code');
  yield* narrator.beat('lifecycle-code',
    camera.reset(1),
    all(tree.opacity(0, 0.7, moveEasing), tree.scale(0.9, 0.8, moveEasing)),
    delay(0.4, arrive(code, 0.94)),
    delay(0.7, code.show(extractRegion(programSource, 'AddRecipe'), 2)),
    delay(2.9, code.focus(3, 6)),
    delay(3.1, lineFocus(3, 6, 1.1, 2)),
    delay(codeDuration * 0.62, code.focus(7, 10)),
    delay(codeDuration * 0.62, lineFocus(7, 10, 1.1, codeDuration * 0.38 - 0.2)),
  );

  // On assignment, the Ristretto is attached and inherits the context.
  const attached = new Pill({text: 'attached', color: 'green', size: 24, opacity: 0});
  camera.add(attached);
  const attachDuration = narrator.duration('lifecycle-attach');
  yield* narrator.beat('lifecycle-attach',
    camera.reset(1),
    leave(code, 0, -120, 0.7),
    delay(0.3, all(tree.opacity(1, 0.6, enterEasing), tree.scale(1, 0.7, moveEasing))),
    delay(1.1, tree.reveal(1)),
    delay(1.9, (function* () {
      attached.position(at('ristretto').add([250, 0]));
      yield* arrive(attached);
    })()),
    delay(2.2, chain(
      travel(camera, at('machine').add([0, 62]), at('recipes').add([0, -62]), 'purple', 0.7),
      travel(camera, at('recipes').add([0, 62]), at('ristretto').add([0, -62]), 'purple', 0.7),
    )),
    delay(1.1, camera.focusOnPoint(new Vector2(200, 140), {zoom: 1.12, duration: attachDuration - 1.3, clear: tree.boxes})),
  );
  code.remove();

  // From now on its writes are tracked; removing the last reference detaches it again.
  const detached = new Pill({text: 'detached', color: 'pink', size: 24, opacity: 0});
  camera.add(detached);
  const trackedDuration = narrator.duration('lifecycle-tracked');
  yield* narrator.beat('lifecycle-tracked',
    chain(
      travel(camera, at('ristretto').add([0, -62]), at('recipes').add([0, 62]), 'purple', 0.7),
      travel(camera, at('recipes').add([0, -62]), at('machine').add([0, 62]), 'purple', 0.7),
    ),
    delay(trackedDuration * 0.5, all(
      tree.node('ristretto').opacity(0.3, 0.6),
      attached.opacity(0, 0.4),
      (function* () {
        detached.position(attached.position());
        yield* arrive(detached);
      })(),
    )),
    delay(trackedDuration - 1.2, all(tree.node('ristretto').opacity(1, 0.6), detached.opacity(0, 0.5), attached.opacity(1, 0.5))),
    camera.focusOnPoint(new Vector2(150, 40), {zoom: 1.08, duration: trackedDuration - 0.2, clear: tree.boxes}),
  );
  detached.remove();

  // A subject referenced from two places stays attached until its last reference is gone.
  const other = new Pill({text: 'another property', color: 'purple', size: 24, opacity: 0});
  camera.add(other);
  const second = new Arrow({curve: {p0: {x: 0, y: 0}, p1: {x: 0, y: 0}, p2: {x: 0, y: 0}, p3: {x: 0, y: 0}}});
  camera.add(second);
  const references = new Pill({text: '2 references', size: 24, opacity: 0});
  camera.add(references);
  const sharedDuration = narrator.duration('lifecycle-shared');
  yield* narrator.beat('lifecycle-shared',
    attached.opacity(0, 0.4),
    (function* () {
      const lungo = at('lungo');
      other.position(lungo.add([-430, 0]));
      const start = other.position().add([150, 0]);
      const end = lungo.add([-140, 0]);
      second.line.p0(start);
      second.line.p1(start.add([60, 0]));
      second.line.p2(end.add([-60, 0]));
      second.line.p3(end);
      references.position(lungo.add([0, 110]));
      yield* chain(arrive(other), second.grow(0.6), arrive(references));
    })(),
    delay(sharedDuration * 0.6, all(second.opacity(0, 0.6), other.opacity(0.35, 0.6), delay(0.3, references.retext('1 reference')))),
    camera.focusOnPoint(new Vector2(-150, 150), {zoom: 1.1, duration: sharedDuration - 0.2, clear: tree.boxes}),
  );

  // React to both moments with the lifecycle events.
  const events = new CodeCard({fileName: 'MachineHost.cs', width: 1400, height: 320, codeFontSize: 28, opacity: 0, scale: 0.94, y: -120});
  camera.add(events);
  const eventsDuration = narrator.duration('lifecycle-events');
  yield* narrator.beat('lifecycle-events',
    camera.reset(1),
    all(leave(tree, 0, 80, 0.7), leave(other, 0, 80, 0.7), leave(references, 0, 80, 0.7), second.opacity(0, 0.4), attached.opacity(0, 0.4)),
    delay(0.4, arrive(events, 0.94)),
    delay(0.7, events.show(extractRegion(hostSource, 'Lifecycle'), 1.4)),
    delay(eventsDuration * 0.55, events.focus(1, 1)),
    delay(eventsDuration * 0.8, events.focus(2, 2)),
    delay(2.2, camera.focusOnPoint(new Vector2(0, -100), {zoom: 1.12, duration: eventsDuration - 2.4, clear: events})),
  );
  tree.remove();
  other.remove();
  second.remove();
  references.remove();
  attached.remove();

  // A lifecycle handler sees every change: attach, reference added, reference removed, detach.
  const handler = new Card({width: 1060, height: 210, y: 190, opacity: 0, scale: 0.94});
  handler.add(<Txt y={-50} fontFamily={fonts.code} fontWeight={600} fontSize={30} fill={palette.text} text={'ILifecycleHandler'} />);
  const flags = ['IsContextAttach', 'IsPropertyReferenceAdded', 'IsPropertyReferenceRemoved', 'IsContextDetach'];
  const flagPills = flags.map((flag, index) => new Pill({text: flag, code: true, size: 22, x: (index % 2 === 0 ? -250 : 250), y: index < 2 ? 4 : 68, opacity: 0.35}));
  flagPills.forEach(pill => handler.add(pill));
  camera.add(handler);
  const handlerDuration = narrator.duration('lifecycle-handler');
  yield* narrator.beat('lifecycle-handler',
    events.unfocus(),
    all(events.y(-230, 0.8, moveEasing), events.opacity(0.6, 0.8)),
    delay(0.4, arrive(handler, 0.94)),
    delay(1.6, sequence(Math.max((handlerDuration - 2.6) / 4, 0.4), ...flagPills.map(pill => all(pill.opacity(1, 0.3), nudge(pill, 1.08))))),
    camera.focusOnPoint(new Vector2(0, 80), {zoom: 1.08, duration: handlerDuration - 0.2}),
  );

  // Live: the attached row arrives in the change stream and the page shows the new recipe.
  const recipe = machineAndStream('recipe');
  camera.add(recipe);
  yield recipe.left;
  yield recipe.right;
  const add = recipe.mark('add');
  const added = recipe.mark('added');
  const liveDuration = narrator.duration('lifecycle-live');
  yield* narrator.beat('lifecycle-live',
    camera.reset(1),
    all(leave(events, 0, -120, 0.7), leave(handler, 0, 120, 0.7)),
    delay(0.3, recipe.arrive()),
    recipe.play(liveDuration, {from: add - 2.4, to: added + 2}),
    delay(1.4, camera.focusOn(() => recipe.pagePoint(recipe.right, 400, 320), {zoom: 1.35, duration: liveDuration - 1.6, clear: recipe.frames})),
  );
  events.remove();
  handler.remove();
  const buttonDuration = narrator.duration('lifecycle-button');
  yield* narrator.beat('lifecycle-button',
    recipe.play(buttonDuration, {from: added + 2}),
    camera.focusOn(() => recipe.pagePoint(recipe.left, 300, 520), {zoom: 1.4, duration: buttonDuration - 0.2, clear: recipe.frames}),
  );

  // Lifecycle handlers run inside a lock: quick work passes, slow work goes to a queue.
  const lock = new Box({label: 'inside the lifecycle lock', width: 620, height: 260, x: -260, y: 0, color: palette.orange, opacity: 0});
  const handlerCard = new Pill({text: 'your handler', color: 'green', size: 28, x: -260, y: 0, opacity: 0});
  const queue = new Box({label: 'queue', width: 300, height: 220, x: 500, y: 0, opacity: 0});
  const quick = new Pill({text: 'cache update', size: 22, x: -760, y: -70, opacity: 0});
  const slow = new Pill({text: 'database call', color: 'pink', size: 22, x: -760, y: 70, opacity: 0});
  [lock, handlerCard, queue, quick, slow].forEach(node => camera.add(node));
  const rulesDuration = narrator.duration('lifecycle-rules');
  yield* narrator.beat('lifecycle-rules',
    camera.reset(1),
    all(recipe.opacity(0, 0.7, moveEasing), recipe.scale(0.9, 0.8, moveEasing)),
    delay(0.5, all(arrive(lock), delay(0.2, arrive(handlerCard)), delay(0.4, arrive(queue)))),
    delay(1.8, chain(
      all(quick.opacity(1, 0.3), quick.x(-260, 1, moveEasing), quick.y(-70, 1, moveEasing)),
      all(quick.x(200, 0.8, moveEasing), quick.opacity(0, 0.8)),
    )),
    delay(rulesDuration * 0.55, chain(
      all(slow.opacity(1, 0.3), slow.x(-260, 1, moveEasing), slow.y(70, 1, moveEasing)),
      all(slow.x(500, 1, moveEasing), slow.y(30, 1, moveEasing)),
    )),
    camera.focusOnPoint(new Vector2(0, 0), {zoom: 1.08, duration: rulesDuration - 0.2}),
  );
  recipe.remove();

  // The packages that follow the graph this way.
  const packages = ['Registry', 'Hosted services', 'Connectors'].map((text, index) =>
    new Pill({text, color: (['blue', 'green', 'orange'] as const)[index], size: 30, x: 900, y: -40, opacity: 0}));
  packages.forEach(pill => camera.add(pill));
  const usesDuration = narrator.duration('lifecycle-uses');
  yield* narrator.beat('lifecycle-uses',
    all(...[lock, handlerCard, queue, quick, slow].map(node => leave(node, 0, -60, 0.6))),
    delay(0.6, sequence(0.5, ...packages.map((pill, index) => all(pill.opacity(1, 0.4), pill.x(-440 + index * 440, 1, moveEasing))))),
    camera.focusOnPoint(new Vector2(0, -40), {zoom: 1.1, duration: usesDuration - 0.2}),
  );
  [lock, handlerCard, queue, quick, slow].forEach(node => node.remove());
});

