import {makeScene2D} from '@revideo/2d';
import {all, chain, delay, sequence, Vector2} from '@revideo/core';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {useTiming} from '../../../theme/variables';
import {serverRow, sourceRow} from '../diagrams/flows';
import {arrive, leave, Pill} from './shared';

export default makeScene2D('recap', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'Recap', kicker: 'What you can do now', colors: ['green', 'cyan', 'blue']});
  view.add(title);
  yield* narrator.beat('recap-title', title.enter());

  const sources = new FlowDiagram({definition: sourceRow, y: -190});
  const servers = new FlowDiagram({definition: serverRow, y: 110});
  camera.add(sources);
  camera.add(servers);
  yield* sources.build();
  yield* servers.build();
  yield* narrator.beat('recap-roles',
    title.exit(),
    delay(0.4, all(sources.reveal(0), sources.reveal(1), sources.reveal(2))),
    delay(1.4, all(servers.reveal(0), servers.reveal(1), servers.reveal(2))),
    delay(2.6, camera.focusOnPoint(new Vector2(0, -40), {zoom: 1.06, duration: narrator.duration('recap-roles') - 2.8})),
  );

  const inbound = new Pill({text: 'in through the property writer', color: 'cyan', size: 24, y: -60, opacity: 0});
  const outbound = new Pill({text: 'out through the change queue', color: 'purple', size: 24, y: 240, opacity: 0});
  camera.add(inbound);
  camera.add(outbound);
  yield* narrator.beat('recap-paths',
    chain(sources.pulse('owner', 'source', 0.6), sources.pulse('source', 'replica', 0.6)),
    delay(0.3, arrive(inbound)),
    camera.focusOnPoint(new Vector2(0, -125), {zoom: 1.06, duration: 1.8}),
    delay(2.4, all(
      arrive(outbound),
      chain(servers.pulse('owner', 'server', 0.6), servers.pulse('server', 'clients', 0.6)),
      camera.focusOnPoint(new Vector2(0, 175), {zoom: 1.08, duration: narrator.duration('recap-paths') - 2.6}),
    )),
  );

  const hooks = ['StartListeningAsync', 'LoadInitialStateAsync', 'WriteChangesAsync'].map((name, index) =>
    new Pill({text: name, code: true, size: 40, y: (index - 1) * 150, opacity: 0}));
  hooks.forEach(hook => camera.add(hook));
  yield* narrator.beat('recap-custom',
    camera.reset(0.9),
    all(leave(sources, 0, -60, 0.5), leave(servers, 0, 60, 0.5), leave(inbound, 0, -60, 0.5), leave(outbound, 0, 60, 0.5)),
    delay(0.5, sequence(0.25, ...hooks.map(hook => arrive(hook)))),
  );

  const outro = new ChapterCard({title: 'Ready to connect', kicker: 'WebSocket, MQTT, OPC UA', colors: ['green', 'cyan', 'blue']});
  view.add(outro);
  yield* narrator.beat('recap-pointers', outro.enter(), delay(1, camera.reset(1)));
});
