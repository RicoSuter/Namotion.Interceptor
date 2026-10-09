import {makeScene2D, Rect, Txt} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2} from '@revideo/core';
import pumpSource from '../../../domain/Coffee/Pump.cs?raw';
import stepsSource from '../sample/Steps.cs?raw';
import {Arrow} from '../../../theme/components/Arrow';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {enterEasing, fonts, moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {machineTree, simulator} from '../diagrams/flows';
import {arrive, leave, nudge, Pill, toLocal, travel} from './shared';

export default makeScene2D('setup', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Setup', kicker: 'Chapter 1'});
  view.add(title);
  yield* narrator.beat('setup-title', title.enter());

  // The machine as a tree of tracked objects.
  const tree = new FlowDiagram({definition: machineTree, y: 40});
  camera.add(tree);
  yield* tree.build();
  yield* narrator.beat('setup-tree',
    title.exit(),
    delay(0.5, tree.reveal(0)),
    delay(1.2, tree.reveal(1)),
    delay(2.6, camera.focusOnPoint(new Vector2(0, 40), {zoom: 1.05, duration: narrator.duration('setup-tree') - 2.8})),
  );

  // The simulator writes to the boiler and the pump.
  const sim = new FlowDiagram({definition: simulator, x: -560, y: -180});
  camera.add(sim);
  yield* sim.build();
  const at = (diagram: FlowDiagram, id: string) => toLocal(camera, diagram.node(id).absolutePosition());
  const simulatorDuration = narrator.duration('setup-simulator');
  yield* narrator.beat('setup-simulator',
    sim.reveal(0),
    delay(0.6, camera.focusOnPoint(new Vector2(-420, 20), {zoom: 1.18, duration: 2, clear: [...tree.boxes, ...sim.boxes]})),
    delay(1, chain(...[0, 1, 2].map(() => all(
      travel(camera, at(sim, 'simulator').add([0, 62]), at(tree, 'boiler').add([0, -62]), 'pink', 0.8),
      delay(0.3, travel(camera, at(sim, 'simulator').add([100, 62]), at(tree, 'pump').add([0, -62]), 'blue', 0.8)),
    )))),
    delay(simulatorDuration - 1.6, nudge(tree.node('boiler'))),
  );

  // Each part is an interceptor subject with partial properties.
  const code = new CodeCard({fileName: 'Pump.cs', width: 1240, height: 480, opacity: 0, scale: 0.94});
  camera.add(code);
  const lineFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => code.linesCenter(from, to).add(code.absolutePosition()).scale(0.5), {zoom, duration, clear: code});
  yield* narrator.beat('setup-subject',
    camera.reset(1),
    all(leave(tree, -200, 0, 0.8), leave(sim, -200, 0, 0.8)),
    delay(0.5, arrive(code, 0.94)),
    delay(0.8, code.show(extractRegion(pumpSource, 'Pump'), 1.6)),
    delay(2.8, code.focus(0, 1)),
    delay(4.4, code.focus(3, 5)),
    delay(4.6, lineFocus(3, 5, 1.12, narrator.duration('setup-subject') - 4.8)),
  );
  tree.opacity(0);
  sim.remove();

  const generated = new Pill({text: 'implemented by the generator', color: 'purple', size: 24, opacity: 0});
  camera.add(generated);
  yield* narrator.beat('setup-generated',
    (function* () {
      const anchor = toLocal(camera, code.linesCenter(4, 4));
      generated.position(anchor.add([330, -110]));
      yield* all(arrive(generated), generated.y(anchor.y + 6 - 60, 0.8, moveEasing));
    })(),
    delay(0.4, lineFocus(3, 5, 1.22, narrator.duration('setup-generated') - 0.6)),
  );

  // The context: full property tracking plus validation.
  const contextCode = extractRegion(stepsSource, 'Context');
  yield* narrator.beat('setup-context',
    camera.reset(1),
    leave(generated, 0, -30, 0.5),
    delay(0.3, code.morph(contextCode, 1.4, 'Steps.cs')),
    delay(2, code.focus(0, 1)),
    delay(3.4, code.focus(2, 2)),
    delay(3.6, lineFocus(2, 2, 1.15, narrator.duration('setup-context') - 3.8)),
  );
  generated.remove();

  // What WithFullPropertyTracking bundles: four features, each also available on its own.
  const features = [
    ['Equality check', 'WithEqualityCheck()'],
    ['Derived property detection', 'WithDerivedPropertyChangeDetection()'],
    ['Change subscriptions', 'WithPropertyChangeSubscriptions()'],
    ['Context inheritance, lifecycle', 'WithContextInheritance()'],
  ];
  const featureX = 470;
  const featureY = (index: number) => -195 + index * 130;
  const pills = features.map(([text], index) => new Pill({text, size: 26, x: featureX, y: featureY(index), opacity: 0}));
  pills.forEach(pill => camera.add(pill));
  const methods = features.map(([, method], index) => (
    <Txt fontFamily={fonts.code} fontSize={24} fill={palette.secondaryText} text={method} x={featureX} y={featureY(index) + 50} opacity={0} />
  ) as Txt);
  methods.forEach(method => camera.add(method));
  const bracket = new Arrow({curve: {p0: {x: -40, y: -60}, p1: {x: 60, y: -60}, p2: {x: 80, y: -60}, p3: {x: 180, y: -60}}});
  camera.add(bracket);
  const bundleDuration = narrator.duration('setup-bundle');
  yield* narrator.beat('setup-bundle',
    camera.reset(1),
    all(code.x(-420, 1, moveEasing), code.scale(0.72, 1, moveEasing)),
    (function* () {
      yield* delay(1.1, (function* () {
        const start = toLocal(camera, code.linesCenter(2, 2)).add([300, 0]);
        bracket.line.p0(start);
        bracket.line.p1(start.add([60, 0]));
        bracket.line.p2(new Vector2(featureX - 280, featureY(1) + 60));
        bracket.line.p3(new Vector2(featureX - 230, featureY(1) + 60));
        yield* bracket.grow(0.6);
      })());
    })(),
    delay(1.6, sequence(Math.max((bundleDuration - 2.6) / 4, 0.5), ...pills.map(pill => arrive(pill)))),
  );
  yield* narrator.beat('setup-individual',
    sequence(0.5, ...methods.map(method => all(method.opacity(1, 0.5, enterEasing), method.y(method.y() - 4, 0.5, enterEasing)))),
    camera.focusOnPoint(new Vector2(featureX - 80, 0), {zoom: 1.12, duration: narrator.duration('setup-individual') - 0.2}),
  );

  // Context inheritance hands the context down the tree.
  const inherited = new FlowDiagram({definition: machineTree, x: 470, y: 20, scale: 0.46});
  camera.add(inherited);
  yield* inherited.build();
  const inheritDuration = narrator.duration('setup-inherit');
  yield* narrator.beat('setup-inherit',
    camera.reset(1),
    all(...pills.map(pill => leave(pill, 60, 0, 0.6)), ...methods.map(method => leave(method, 60, 0, 0.6)), bracket.opacity(0, 0.5)),
    delay(0.3, code.focus(5, 5)),
    delay(0.6, all(inherited.reveal(0), delay(0.3, inherited.reveal(1)))),
    delay(2.2, sequence(0.25, ...['boiler', 'pump', 'tank', 'hopper', 'recipes'].map(id => inherited.pulse('machine', id, 0.8)))),
    delay(2.2, camera.focusOnPoint(new Vector2(180, 0), {zoom: 1.08, duration: inheritDuration - 2.4})),
  );
  pills.forEach(pill => pill.remove());
  methods.forEach(method => method.remove());
  bracket.remove();
  tree.remove();

  // Validation: the boiler's allowed temperature range.
  const range = new Pill({text: 'TargetTemperature 85 to 96 °C', color: 'pink', size: 26, opacity: 0});
  camera.add(range);
  yield* narrator.beat('setup-validation',
    camera.reset(1),
    leave(inherited, 60, 0, 0.6),
    delay(0.3, all(code.x(-200, 1, moveEasing), code.scale(0.85, 1, moveEasing))),
    delay(1.1, code.focus(3, 3)),
    delay(1.4, (function* () {
      const anchor = toLocal(camera, code.linesCenter(3, 3));
      range.position(anchor.add([700, 0]));
      yield* arrive(range);
    })()),
    delay(1.6, camera.focusOnPoint(new Vector2(180, -40), {zoom: 1.1, duration: narrator.duration('setup-validation') - 1.8})),
  );
  inherited.remove();

  // Transactions come later: a placeholder on the empty line.
  const placeholder = (
    <Rect layout padding={[10, 22]} gap={16} alignItems={'center'} radius={14} stroke={palette.separator} lineWidth={3}
      lineDash={[10, 10]} opacity={0}>
      <Txt fontFamily={fonts.code} fontSize={24} fill={palette.secondaryText} text={'.WithTransactions()'} />
      <Txt fontFamily={fonts.text} fontWeight={600} fontSize={22} fill={palette.blue} text={'later'} />
    </Rect>
  ) as Rect;
  camera.add(placeholder);
  yield* narrator.beat('setup-opt-in',
    code.focus(3, 4),
    (function* () {
      const anchor = toLocal(camera, code.linesCenter(4, 4));
      placeholder.position(anchor.add([-90, 4]));
      yield* all(placeholder.opacity(1, 0.5, enterEasing), placeholder.x(anchor.x - 60, 0.6, enterEasing));
    })(),
    delay(1.2, camera.reset(narrator.duration('setup-opt-in') - 1.4)),
    delay(0.9, leave(range, 40, 0, 0.6)),
  );
});
