import {makeScene2D, type Node} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2, waitFor, type ThreadGenerator} from '@revideo/core';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {SequenceDiagram} from '../../../theme/components/SequenceDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {moveEasing} from '../../../theme/style';
import {useTiming} from '../../../theme/variables';
import {serverRow, twoPaths} from '../diagrams/flows';
import {arrive, band, leave, Pill} from './shared';

/** Lane x and row y of a sequence diagram, matching its own layout, in the diagram's coordinates. */
function sequenceGrid(participants: number, width: number, height: number, rowHeight: number) {
  const spacing = width / (participants - 1);
  return {
    lane: (index: number) => -width / 2 + index * spacing,
    row: (index: number) => -height / 2 + 38 + 70 + index * rowHeight,
    /** Top of the participant headers. */
    top: -height / 2,
  };
}

/** Sends a message, then collects the arrow and label it added, so they can be faded later. */
function* messageNodes(diagram: SequenceDiagram, send: () => ThreadGenerator, added: Node[]): ThreadGenerator {
  const before = new Set(diagram.children());
  yield* send();
  added.push(...diagram.children().filter(child => !before.has(child)));
}

/** Fades nodes collected while the scene runs; the list is read when the fade starts, not when it is created. */
function* fadeCollected(nodes: Node[], duration: number): ThreadGenerator {
  yield* all(...nodes.map(node => node.opacity(0, duration, moveEasing)));
}

/** Diagram centers that put the headers and the used message rows in the middle of the frame. */
const inboundY = 20;
const outboundY = 60;

export default makeScene2D('flow', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Data flow', kicker: 'Chapter 2'});
  view.add(title);
  yield* narrator.beat('flow-title', title.enter());

  // Inbound: external system, source, property writer, subject property.
  const inboundSize = {width: 1440, height: 560, rowHeight: 110};
  const inbound = new SequenceDiagram({
    participants: [
      {id: 'external', label: 'External system', color: 'orange'},
      {id: 'source', label: 'Source', color: 'blue'},
      {id: 'writer', label: 'Property writer', color: 'cyan'},
      {id: 'boiler', label: 'Boiler', color: 'pink'},
    ],
    ...inboundSize,
    y: inboundY,
  });
  const inboundGrid = sequenceGrid(4, inboundSize.width, inboundSize.height, inboundSize.rowHeight);
  /** Camera target between two lanes, halfway down from the headers to a message row, in camera coordinates. */
  const inboundFocus = (fromLane: number, toLane: number, row: number) => new Vector2(
    (inboundGrid.lane(fromLane) + inboundGrid.lane(toLane)) / 2, inboundY + (inboundGrid.top + inboundGrid.row(row)) / 2);
  camera.add(inbound);
  yield* narrator.beat('flow-in-intro',
    title.exit(),
    delay(0.4, chain(inbound.appear(), waitFor(0.6), inbound.message('external', 'source', 'Temperature = 93'))),
    delay(1.2, camera.focusOnPoint(inboundFocus(0, 1, 0), {zoom: 1.15, duration: narrator.duration('flow-in-intro') - 1.4})),
  );
  yield* narrator.beat('flow-in-writer',
    inbound.message('source', 'writer', 'Write(update)'),
    camera.focusOnPoint(inboundFocus(1, 2, 1), {zoom: 1.15, duration: narrator.duration('flow-in-writer') - 0.2}),
  );

  const buffered = new Pill({text: 'held while loading', color: 'cyan', size: 24, x: inboundGrid.lane(2) + 230, y: inboundGrid.row(1) - 90, opacity: 0, scale: 0.9});
  inbound.add(buffered);
  yield* narrator.beat('flow-in-apply',
    inbound.message('writer', 'boiler', 'Temperature = 93'),
    camera.focusOnPoint(inboundFocus(2, 3, 2).add([-60, 0]), {zoom: 1.15, duration: narrator.duration('flow-in-apply') - 0.2}),
    delay(2.4, chain(arrive(buffered), buffered.scale(1.08, 0.3).to(1, 0.4))),
  );

  const stamp = new Pill({text: 'from source', color: 'blue', size: 24, x: inboundGrid.lane(3) - 120, y: inboundGrid.row(2) + 50, opacity: 0, scale: 0.9});
  inbound.add(stamp);
  const echo: Node[] = [];
  yield* narrator.beat('flow-in-stamp',
    arrive(stamp),
    camera.focusOnPoint(inboundFocus(1, 3, 3).add([0, 60]), {zoom: 1.12, duration: 1.8}),
    delay(1.6, messageNodes(inbound, () => inbound.message('boiler', 'source', 'not sent back', {reply: true}), echo)),
    delay(3.4, fadeCollected(echo, 0.8)),
  );
  yield* narrator.beat('flow-in-why',
    camera.reset(1.6),
    delay(1.2, chain(buffered.scale(1.12, 0.3).to(1, 0.4), waitFor(0.3), buffered.scale(1.12, 0.3).to(1, 0.4))),
    delay(1.4, inbound.activate('writer')),
  );

  // Outbound: your code, the boiler, the change queue, the source, the external system.
  const outboundSize = {width: 1520, height: 720, rowHeight: 92};
  const outbound = new SequenceDiagram({
    participants: [
      {id: 'code', label: 'Your code', color: 'green'},
      {id: 'boiler', label: 'Boiler', color: 'pink'},
      {id: 'queue', label: 'Change queue', color: 'purple'},
      {id: 'source', label: 'Source', color: 'blue'},
      {id: 'external', label: 'External system', color: 'orange'},
    ],
    ...outboundSize,
    y: outboundY,
  });
  const outboundGrid = sequenceGrid(5, outboundSize.width, outboundSize.height, outboundSize.rowHeight);
  const outboundFocus = (fromLane: number, toLane: number, row: number) => new Vector2(
    (outboundGrid.lane(fromLane) + outboundGrid.lane(toLane)) / 2, outboundY + (outboundGrid.top + outboundGrid.row(row)) / 2);
  yield* narrator.beat('flow-out-intro',
    leave(inbound, 0, -80, 0.7),
    delay(0.6, chain(
      (function* () {
        camera.add(outbound);
        yield* outbound.appear();
      })(),
      outbound.message('code', 'boiler', 'TargetTemperature = 95'),
    )),
    delay(1.6, camera.focusOnPoint(outboundFocus(0, 1, 0), {zoom: 1.2, duration: narrator.duration('flow-out-intro') - 1.8})),
  );
  inbound.remove();
  yield* narrator.beat('flow-out-queue',
    outbound.message('boiler', 'queue', 'change'),
    camera.focusOnPoint(outboundFocus(1, 2, 1), {zoom: 1.2, duration: narrator.duration('flow-out-queue') - 0.2}),
  );
  yield* narrator.beat('flow-out-write',
    outbound.message('queue', 'source', 'WriteChangesAsync'),
    delay(2.2, outbound.message('source', 'external', 'send')),
    camera.focusOnPoint(outboundFocus(2, 4, 3), {zoom: 1.15, duration: narrator.duration('flow-out-write') - 0.2}),
  );

  const chipNames = ['Temperature', 'Pressure', 'State'];
  const chipY = outboundGrid.row(2) + 46;
  const chips = chipNames.map((name, index) =>
    new Pill({text: name, color: 'purple', size: 22, x: outboundGrid.lane(2) + (index - 1) * 170, y: chipY + 40, opacity: 0, scale: 0.9}));
  chips.forEach(chip => outbound.add(chip));
  yield* narrator.beat('flow-out-batch',
    camera.focusOnPoint(new Vector2(190, outboundY + chipY + 20), {zoom: 1.15, duration: 1.6}),
    sequence(0.25, ...chips.map(chip => arrive(chip))),
    delay(1.4, all(...chips.map((chip, index) => chip.position(new Vector2(outboundGrid.lane(2) + 110, chipY + 10 + index * 48), 0.7, moveEasing)))),
    delay(2.4, all(...chips.map(chip => chip.x(outboundGrid.lane(3) - 110, 1.2, moveEasing)))),
    delay(3.7, all(...chips.map(chip => chip.opacity(0, 0.5)))),
  );
  chips.forEach(chip => chip.remove());

  const localFirst = band({
    width: outboundGrid.lane(4) - outboundGrid.lane(1) + 200,
    height: outboundGrid.row(3) - outboundGrid.row(0) + 120,
    color: 'green',
    x: (outboundGrid.lane(4) + outboundGrid.lane(1)) / 2,
    y: (outboundGrid.row(3) + outboundGrid.row(0)) / 2 - 20,
  });
  const localFirstLabel = new Pill({text: 'local first', color: 'green', size: 26, x: outboundGrid.lane(4) - 40, y: outboundGrid.row(0) - 40, opacity: 0, scale: 0.9});
  outbound.insert(localFirst, 0);
  outbound.add(localFirstLabel);
  yield* narrator.beat('flow-local-first',
    camera.reset(1.4),
    delay(0.8, localFirst.opacity(0.1, 0.8, moveEasing)),
    delay(1.2, arrive(localFirstLabel)),
    delay(2.4, camera.focusOnPoint(new Vector2(190, outboundY + (outboundGrid.top + outboundGrid.row(3)) / 2), {zoom: 1.08, duration: narrator.duration('flow-local-first') - 2.6})),
  );
  yield* narrator.beat('flow-no-wait',
    outbound.activate('code'),
    delay(1.2, outbound.activate('boiler')),
    camera.focusOnPoint(outboundFocus(0, 1, 0), {zoom: 1.18, duration: narrator.duration('flow-no-wait') - 0.2}),
  );
  yield* narrator.beat('flow-retry',
    all(localFirst.opacity(0, 0.6), localFirstLabel.opacity(0, 0.6)),
    camera.focusOnPoint(new Vector2((outboundGrid.lane(3) + outboundGrid.lane(4)) / 2, outboundY + (outboundGrid.row(4) + outboundGrid.row(5)) / 2), {zoom: 1.2, duration: narrator.duration('flow-retry') - 0.2}),
    delay(0.4, outbound.message('external', 'source', 'failed', {reply: true})),
    delay(2.6, outbound.message('source', 'external', 'retry')),
  );

  // Servers: the same pattern from the other side.
  const servers = new FlowDiagram({definition: serverRow});
  camera.add(servers);
  yield* servers.build();
  yield* narrator.beat('flow-server',
    camera.reset(1.2),
    leave(outbound, 0, -80, 0.7),
    delay(0.6, chain(
      servers.reveal(0), servers.reveal(1), servers.reveal(2),
      servers.pulse('owner', 'server', 0.7), servers.pulse('server', 'clients', 0.7),
    )),
  );
  outbound.remove();
  yield* narrator.beat('flow-server-sample',
    servers.retext('server', {label: 'WebSocket handler', detail: 'embedded server'}),
    camera.focusOn(servers.node('server'), {zoom: 1.3, duration: 1.8}),
    delay(1.8, servers.node('server').scale(1.08, 0.3).to(1, 0.4)),
  );
  yield* narrator.beat('flow-server-in',
    camera.reset(1.4),
    servers.reveal(3),
    delay(1.2, chain(
      servers.pulse('clients', 'server', 0.7), servers.pulse('server', 'owner', 0.7),
      servers.pulse('owner', 'server', 0.7), servers.pulse('server', 'clients', 0.7),
    )),
  );

  // The two paths side by side.
  const paths = new FlowDiagram({definition: twoPaths});
  camera.add(paths);
  yield* paths.build();
  yield* narrator.beat('flow-two-paths',
    leave(servers, 0, 80, 0.6),
    delay(0.4, chain(paths.reveal(0), paths.reveal(1), paths.reveal(2))),
    delay(1.8, all(paths.pulse('external', 'model', 0.9), delay(0.5, paths.pulse('model', 'external', 0.9)))),
    camera.focusOnPoint(new Vector2(0, 0), {zoom: 1.15, duration: narrator.duration('flow-two-paths') - 0.2}),
  );
});
