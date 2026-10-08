import {Circle, makeScene2D} from '@revideo/2d';
import {all, chain, delay, sequence, spring, Vector2, waitFor} from '@revideo/core';
import clientSource from '../sample/Client/Program.cs?raw';
import {Arrow} from '../../../theme/components/Arrow';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {sideCurve} from '../../../theme/geometry';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {arrivalSpring, moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {bridge, machineTree, serverRow, sourceNode, sourceRow} from '../diagrams/flows';
import {arrive, leave, Pill, toLocal, travel} from './shared';

/** Offset of the machine row from the center of the machine tree, for placing a source beside it. */
const machineRowY = -127;
/** Center of the mirror's tree and of the second source beside it in the claim beats. */
const claimsY = -20;
const otherY = 230;

export default makeScene2D('roles', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Sources and servers', kicker: 'Chapter 1'});
  view.add(title);
  yield* narrator.beat('roles-title', title.enter());

  // The coffee machine as a tree of tracked objects.
  const tree = new FlowDiagram({definition: machineTree});
  camera.add(tree);
  yield* tree.build();
  yield* narrator.beat('roles-tree',
    title.exit(),
    delay(0.5, chain(tree.reveal(0), waitFor(0.3), tree.reveal(1))),
    delay(2.6, camera.focusOnPoint(new Vector2(0, 0), {zoom: 1.08, duration: narrator.duration('roles-tree') - 2.8})),
  );

  // The simulator writes the boiler temperature every tenth of a second.
  const simulator = new FlowDiagram({definition: sourceNode('Simulator', 'every 100 ms', 'orange'), x: -620, y: machineRowY});
  camera.add(simulator);
  yield* simulator.build();
  const boiler = tree.node('boiler');
  const simulatorBottom = toLocal(camera, simulator.node('source').absolutePosition()).add([0, 62]);
  const boilerTop = toLocal(camera, boiler.absolutePosition()).add([0, -62]);
  const heatArrow = new Arrow({curve: sideCurve(simulatorBottom, 'bottom', boilerTop, 'top'), color: palette.orange});
  camera.add(heatArrow);
  const heat = (temperature: number) => chain(
    travel(camera, simulatorBottom, boilerTop, 'orange', 0.7),
    all(tree.retext('boiler', {detail: `Temperature ${temperature} °C`}, 0.4), boiler.scale(1.06, 0.15).to(1, 0.3)),
  );
  yield* narrator.beat('roles-writes',
    simulator.reveal(0),
    delay(0.5, heatArrow.grow(0.6)),
    delay(0.6, chain(
      camera.focusOn(boiler, {zoom: 1.25, duration: 2}),
      camera.focusOn(() => boiler.absolutePosition().add(simulator.absolutePosition()).scale(0.5), {zoom: 1.35, duration: narrator.duration('roles-writes') - 2.8}),
    )),
    delay(1.2, chain(heat(61), heat(65), heat(69), heat(73))),
  );

  // The tree collapses into one model node, bridged to an external system.
  const bridgeDiagram = new FlowDiagram({definition: bridge});
  camera.add(bridgeDiagram);
  yield* bridgeDiagram.build();
  const modelPosition = toLocal(camera, bridgeDiagram.node('model').absolutePosition());
  const collapse = (node: FlowDiagram) => all(
    node.position(modelPosition, 0.9, moveEasing), node.scale(0.25, 0.9, moveEasing), node.opacity(0, 0.8, moveEasing),
  );
  yield* narrator.beat('roles-bridge',
    camera.reset(1),
    delay(0.4, all(collapse(tree), collapse(simulator), heatArrow.opacity(0, 0.4))),
    delay(1.1, chain(
      bridgeDiagram.reveal(0),
      bridgeDiagram.reveal(1),
      bridgeDiagram.pulse('model', 'connector', 0.8),
      bridgeDiagram.pulse('connector', 'external', 0.8),
      bridgeDiagram.pulse('external', 'connector', 0.8),
      bridgeDiagram.pulse('connector', 'model', 0.8),
    )),
  );
  tree.remove();
  simulator.remove();
  heatArrow.remove();

  const question = new Pill({text: 'Who owns the data?', size: 44, y: -110, opacity: 0, scale: 0.9});
  camera.add(question);
  yield* narrator.beat('roles-question',
    bridgeDiagram.y(70, 0.8, moveEasing),
    delay(0.2, arrive(question)),
    delay(0.6, all(bridgeDiagram.node('model').scale(1.08, 0.3).to(1, 0.4), bridgeDiagram.node('external').scale(1.08, 0.3).to(1, 0.4))),
    delay(1.6, all(bridgeDiagram.pulse('external', 'connector', 0.9), bridgeDiagram.pulse('model', 'connector', 0.9))),
  );

  // The two roles, one row each.
  const sources = new FlowDiagram({definition: sourceRow, y: -165});
  const servers = new FlowDiagram({definition: serverRow, y: 165});
  camera.add(sources);
  camera.add(servers);
  yield* sources.build();
  yield* servers.build();
  yield* narrator.beat('roles-source',
    leave(bridgeDiagram, 0, 60, 0.6),
    leave(question, 0, -60, 0.6),
    delay(0.4, camera.focusOnPoint(new Vector2(0, -165), {zoom: 1.12, duration: narrator.duration('roles-source') - 0.6})),
    delay(0.5, chain(
      sources.reveal(0), sources.reveal(1), sources.reveal(2),
      sources.pulse('owner', 'source', 0.7), sources.pulse('source', 'replica', 0.7),
    )),
  );
  bridgeDiagram.remove();
  question.remove();
  yield* narrator.beat('roles-server',
    camera.focusOnPoint(new Vector2(0, 165), {zoom: 1.12, duration: narrator.duration('roles-server') - 0.3}),
    chain(
      servers.reveal(0), servers.reveal(1), servers.reveal(2),
      servers.pulse('owner', 'server', 0.7), servers.pulse('server', 'clients', 0.7),
    ),
  );
  const nudge = (diagram: FlowDiagram, id: string) => diagram.node(id).scale(1.08, 0.3).to(1, 0.45);
  yield* narrator.beat('roles-convention',
    camera.reset(1.2),
    delay(0.8, chain(nudge(sources, 'owner'), waitFor(0.2), nudge(servers, 'owner'))),
    delay(2.4, all(
      chain(sources.pulse('owner', 'source', 0.8), sources.pulse('source', 'replica', 0.8)),
      chain(servers.pulse('owner', 'server', 0.8), servers.pulse('server', 'clients', 0.8)),
    )),
  );

  const packages = ['OPC UA', 'MQTT', 'WebSocket'].map((name, index) =>
    new Pill({text: name, color: 'blue', x: (index - 1) * 300, y: 330, opacity: 0, scale: 0.9}));
  packages.forEach(pill => camera.add(pill));
  yield* narrator.beat('roles-packages',
    delay(0.4, sequence(0.35, ...packages.map(pill => arrive(pill)))),
    camera.focusOnPoint(new Vector2(0, 140), {zoom: 1.08, duration: narrator.duration('roles-packages') - 0.3}),
    delay(2.2, all(nudge(sources, 'source'), nudge(servers, 'server'))),
  );

  yield* narrator.beat('roles-sample',
    all(...packages.map(pill => leave(pill, 0, 40, 0.5))),
    delay(0.3, all(
      servers.retext('server', {label: 'WebSocket server', detail: 'machine process'}),
      sources.retext('source', {label: 'WebSocket client', detail: 'mirror process'}),
    )),
    delay(1.2, chain(
      camera.focusOn(servers.node('server'), {zoom: 1.2, duration: 1.6}),
      camera.focusOn(() => sources.node('source').absolutePosition(), {zoom: 1.15, duration: 1.8}),
    )),
  );
  packages.forEach(pill => pill.remove());

  // The mirror's tree, claimed property by property by its client source.
  const claims = new FlowDiagram({definition: machineTree, x: -180, y: claimsY});
  const client = new FlowDiagram({definition: sourceNode('WebSocket client', 'source', 'blue'), x: 700, y: claimsY + machineRowY});
  camera.add(claims);
  camera.add(client);
  yield* claims.build();
  yield* client.build();
  const claimed = ['machine', 'boiler', 'pump', 'tank', 'hopper'];
  const dots = claimed.map(id => {
    const node = claims.node(id);
    const dot = (<Circle layout={false} size={22} fill={palette.blue} x={node.width() / 2 - 24} y={-node.height() / 2 + 24} scale={0} />) as Circle;
    node.add(dot);
    return dot;
  });
  const clientLeft = () => toLocal(camera, client.node('source').absolutePosition()).add([-160, 0]);
  const claim = (id: string, dot: Circle) => chain(
    travel(camera, clientLeft(), toLocal(camera, claims.node(id).absolutePosition()), 'blue', 0.7),
    spring(arrivalSpring, 0, 1, value => dot.scale(value)),
  );
  yield* narrator.beat('roles-claim',
    camera.reset(1.1),
    all(leave(sources, 0, -60), leave(servers, 0, 60)),
    delay(0.6, all(claims.reveal(0), claims.reveal(1), client.reveal(0))),
    delay(1.0, claims.retext('machine', {detail: 'the mirror'})),
    delay(2.2, sequence(0.3, ...claimed.map((id, index) => claim(id, dots[index])))),
    delay(1.2, camera.focusOnPoint(new Vector2(60, claimsY), {zoom: 1.05, duration: narrator.duration('roles-claim') - 1.4})),
  );
  sources.remove();
  servers.remove();

  const other = new FlowDiagram({definition: sourceNode('Another source', 'source', 'orange'), x: 700, y: otherY});
  camera.add(other);
  yield* other.build();
  const hopper = claims.node('hopper');
  const otherLeft = new Vector2(540, otherY);
  const hopperRight = toLocal(camera, hopper.absolutePosition()).add([150, 0]);
  const rejected = new Arrow({curve: sideCurve(otherLeft, 'left', hopperRight, 'right'), color: palette.pink, dashed: true});
  camera.add(rejected);
  yield* narrator.beat('roles-claim-why',
    other.reveal(0),
    delay(0.4, rejected.grow(0.7)),
    delay(1.2, all(rejected.opacity(0, 0.6), dots[4].scale(1.4, 0.2).to(1, 0.3))),
    camera.focusOnPoint(new Vector2(380, (claimsY + otherY) / 2 + 40), {zoom: 1.2, duration: 1.4}),
  );
  rejected.remove();

  // Any property can tell which source owns it.
  const code = new CodeCard({fileName: 'Client/Program.cs', width: 1240, height: 620, opacity: 0, scale: 0.94});
  camera.add(code);
  const stateRegion = extractRegion(clientSource, 'SourceState');
  yield* narrator.beat('roles-try-get',
    camera.reset(1),
    all(leave(claims, -200, 0), leave(client, -200, 0), leave(other, -200, 0)),
    delay(0.4, arrive(code, 0.94)),
    delay(0.7, code.show(stateRegion, 2.6)),
  );
  claims.remove();
  client.remove();
  other.remove();
  yield* narrator.beat('roles-state',
    code.focus(4, 5),
    delay(0.4, camera.focusOn(() => code.linesCenter(4, 5).add(code.absolutePosition()).scale(0.5), {zoom: 1.2, duration: 2})),
  );
  yield* narrator.beat('roles-state-use',
    code.focus(6, 6),
    delay(2.2, code.focus(8, 8)),
    delay(3.4, all(camera.reset(1.6), code.unfocus())),
  );
});
