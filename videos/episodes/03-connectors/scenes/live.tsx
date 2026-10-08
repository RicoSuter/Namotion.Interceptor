import {makeScene2D} from '@revideo/2d';
import {all, chain, delay, Vector2} from '@revideo/core';
import machineSource from '../../../domain/Coffee/CoffeeMachine.cs?raw';
import clientSource from '../sample/Client/Program.cs?raw';
import serverSource from '../sample/Server/Program.cs?raw';
import {Camera} from '../../../theme/components/Camera';
import {ChapterCard} from '../../../theme/components/ChapterCard';
import {CodeCard} from '../../../theme/components/CodeCard';
import {FlowDiagram} from '../../../theme/components/FlowDiagram';
import {SequenceDiagram} from '../../../theme/components/SequenceDiagram';
import {Terminal} from '../../../theme/components/Terminal';
import {waitForFonts} from '../../../theme/fonts';
import {Narrator} from '../../../theme/narrator';
import {extractRegion} from '../../../theme/regions';
import {moveEasing} from '../../../theme/style';
import {useTerminal, useTiming} from '../../../theme/variables';
import {livePath} from '../diagrams/flows';
import {arrive, leave, MachineWindows, Pill} from './shared';

/** Protocol diagram: tall enough for eight messages, centered on its headers and rows. */
const protocolHeight = 880;
const protocolRow = 90;
const protocolY = 90;

export default makeScene2D('live', function* (view) {
  yield* waitForFonts();
  const narrator = new Narrator(view, useTiming());
  const camera = new Camera({});
  view.add(camera);

  const title = new ChapterCard({title: 'The live sample', kicker: 'Chapter 3'});
  view.add(title);
  yield* narrator.beat('live-title', title.enter());

  // The server: context, machine, simulator and the embedded WebSocket handler.
  const code = new CodeCard({fileName: 'Server/Program.cs', width: 1560, height: 700, codeFontSize: 28, opacity: 0, scale: 0.94});
  camera.add(code);
  const lineFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => code.linesCenter(from, to).add(code.absolutePosition()).scale(0.5), {zoom, duration, clear: code});
  yield* narrator.beat('live-server-code',
    title.exit(),
    delay(0.3, arrive(code, 0.94)),
    delay(0.6, code.show(extractRegion(serverSource, 'ServerSetup'), 2.6)),
    delay(3.6, code.focus(0, 4)),
    delay(5.8, code.focus(6, 7)),
  );
  yield* narrator.beat('live-server-handler',
    code.focus(9, 9),
    delay(0.3, lineFocus(9, 9, 1.25, 2.2)),
    delay(4.5, code.scale(1.02, 0.4).to(1, 0.5)),
  );
  yield* narrator.beat('live-server-map',
    camera.reset(1),
    delay(0.3, code.morph(extractRegion(serverSource, 'MapHandler'), 1.4)),
    delay(2.2, code.focus(0, 1)),
    delay(2.6, lineFocus(0, 1, 1.15, 2.4)),
  );

  // The client: its own machine and a WebSocket client source.
  yield* narrator.beat('live-client-code',
    camera.reset(1),
    delay(0.4, code.morph(extractRegion(clientSource, 'ClientSetup'), 1.6, 'Client/Program.cs')),
    delay(3.2, code.focus(0, 6)),
    delay(3.4, lineFocus(0, 6, 1.06, narrator.duration('live-client-code') - 3.6)),
  );
  yield* narrator.beat('live-client-classes',
    code.focus(6, 6),
    lineFocus(6, 6, 1.12, 2.4),
  );
  yield* narrator.beat('live-client-source',
    code.focus(8, 11),
    lineFocus(8, 11, 1.12, 1.8),
    delay(2.4, lineFocus(10, 10, 1.2, 2.6)),
  );

  const terminal = new Terminal({transcript: useTerminal('run-client'), title: 'client', width: 1500, opacity: 0, scale: 0.94});
  camera.add(terminal);
  yield* narrator.beat('live-terminal',
    camera.reset(0.9),
    leave(code, 0, -200, 0.8),
    delay(0.5, arrive(terminal, 0.94)),
    delay(0.8, terminal.run(narrator.duration('live-terminal') - 1.8)),
    delay(2, camera.focusOnPoint(new Vector2(0, 0), {zoom: 1.08, duration: narrator.duration('live-terminal') - 2.2})),
  );
  code.remove();

  // The WebSocket protocol between client and server.
  const protocol = new SequenceDiagram({
    participants: [
      {id: 'client', label: 'Client', color: 'cyan'},
      {id: 'server', label: 'Server', color: 'purple'},
    ],
    width: 900, height: protocolHeight, rowHeight: protocolRow, y: protocolY,
  });
  /** Camera target for a message row, in camera coordinates. */
  const rowY = (row: number) => protocolY - protocolHeight / 2 + 38 + 70 + row * protocolRow;
  yield* narrator.beat('live-protocol-hello',
    leave(terminal, 0, -100, 0.6),
    delay(0.4, chain(
      (function* () {
        camera.add(protocol);
        yield* protocol.appear();
      })(),
      protocol.message('client', 'server', 'Hello'),
    )),
    delay(0.6, camera.focusOnPoint(new Vector2(0, rowY(0) - 60), {zoom: 1.12, duration: narrator.duration('live-protocol-hello') - 0.8, clear: protocol.headers})),
  );
  terminal.remove();
  yield* narrator.beat('live-protocol-welcome',
    protocol.message('server', 'client', 'Welcome: state, sequence 5', {reply: true}),
    camera.focusOnPoint(new Vector2(0, rowY(1) - 40), {zoom: 1.12, duration: narrator.duration('live-protocol-welcome') - 0.2, clear: protocol.headers}),
  );
  yield* narrator.beat('live-protocol-updates',
    protocol.message('server', 'client', 'Update, sequence 6'),
    delay(1.4, protocol.message('server', 'client', 'Update, sequence 7')),
    camera.focusOnPoint(new Vector2(0, rowY(2)), {zoom: 1.12, duration: narrator.duration('live-protocol-updates') - 0.2, clear: protocol.headers}),
  );
  yield* narrator.beat('live-protocol-client-update',
    protocol.message('client', 'server', 'Update: State = Brewing'),
    camera.focusOnPoint(new Vector2(0, rowY(3)), {zoom: 1.12, duration: narrator.duration('live-protocol-client-update') - 0.2, clear: protocol.headers}),
  );
  yield* narrator.beat('live-protocol-heartbeat',
    protocol.message('server', 'client', 'Heartbeat, sequence 7', {reply: true}),
    camera.focusOnPoint(new Vector2(0, rowY(4)), {zoom: 1.12, duration: 2, clear: protocol.headers}),
    delay(2.4, camera.focusOnPoint(new Vector2(0, rowY(2)), {zoom: 0.92, duration: narrator.duration('live-protocol-heartbeat') - 2.6, clear: protocol.headers})),
  );
  const gap = new Pill({text: 'gap or restart', color: 'pink', size: 24, x: -700, y: rowY(6), opacity: 0, scale: 0.9});
  camera.add(gap);
  yield* narrator.beat('live-protocol-reconnect',
    camera.focusOnPoint(new Vector2(0, rowY(4)), {zoom: 1.0, duration: 1.4, clear: protocol.headers}),
    arrive(gap),
    delay(0.8, protocol.message('client', 'server', 'Hello')),
    delay(2.4, protocol.message('server', 'client', 'Welcome: state, sequence 9', {reply: true})),
  );

  // The whole path through both processes.
  const path = new FlowDiagram({definition: livePath});
  camera.add(path);
  yield* path.build();
  yield* narrator.beat('live-path',
    camera.reset(1),
    all(leave(protocol, 0, -100, 0.7), leave(gap, 0, -100, 0.7)),
    delay(0.5, chain(path.reveal(0), path.reveal(1))),
    delay(2.6, chain(path.pulse('simulator', 'machine', 0.7), all(path.pulse('machine', 'handler', 0.7), path.pulse('machine', 'serverPage', 0.7)))),
    delay(2.6, camera.focusOnPoint(new Vector2(-200, 0), {zoom: 1.08, duration: narrator.duration('live-path') - 2.8})),
  );
  protocol.remove();
  gap.remove();
  yield* narrator.beat('live-path-client',
    chain(path.reveal(2), path.reveal(3)),
    delay(1.6, chain(path.pulse('handler', 'source', 0.6), path.pulse('source', 'mirror', 0.6), path.pulse('mirror', 'clientPage', 0.6))),
    camera.focusOnPoint(new Vector2(250, 0), {zoom: 1.08, duration: narrator.duration('live-path-client') - 0.2}),
  );

  // Both processes heating up, side by side.
  const heat = new MachineWindows('heat');
  camera.add(heat);
  yield heat.server;
  yield heat.client;
  const warm = heat.mark('warm');
  const ready = heat.mark('ready');
  const heatDuration = narrator.duration('live-heat');
  yield* narrator.beat('live-heat',
    camera.reset(1),
    all(path.opacity(0, 0.7, moveEasing), path.scale(0.6, 0.8, moveEasing)),
    delay(0.4, heat.arrive()),
    heat.play(heatDuration, {from: heat.mark('open'), to: warm}),
    delay(1.4, chain(
      camera.focusOn(() => heat.pagePoint(heat.server, 400, 360), {zoom: 1.1, duration: 2.4, clear: heat.frames}),
      camera.focusOn(() => heat.pagePoint(heat.client, 400, 360), {zoom: 1.1, duration: heatDuration - 4, clear: heat.frames}),
    )),
  );
  path.remove();
  yield* narrator.beat('live-heat-sync',
    heat.play(narrator.duration('live-heat-sync'), {from: warm, to: ready - 2.1}),
    camera.focusOnPoint(new Vector2(0, -30), {zoom: 1.1, duration: narrator.duration('live-heat-sync') - 0.2, clear: heat.frames}),
  );
  yield* narrator.beat('live-ready',
    heat.play(narrator.duration('live-ready'), {from: ready - 2.1}),
    camera.reset(1.6),
    delay(2.2, camera.focusOn(() => heat.pagePoint(heat.client, 400, 360), {zoom: 1.15, duration: narrator.duration('live-ready') - 2.4, clear: heat.frames})),
  );

  // The brew endpoint and the method it calls.
  const brewCode = new CodeCard({fileName: 'Client/Program.cs', width: 1560, height: 700, codeFontSize: 28, opacity: 0, scale: 0.94});
  camera.add(brewCode);
  const brewFocus = (from: number, to: number, zoom: number, duration: number) =>
    camera.focusOn(() => brewCode.linesCenter(from, to).add(brewCode.absolutePosition()).scale(0.5), {zoom, duration, clear: brewCode});
  yield* narrator.beat('live-brew-code',
    camera.reset(1),
    all(heat.opacity(0, 0.7, moveEasing), heat.scale(0.9, 0.8, moveEasing)),
    delay(0.5, arrive(brewCode, 0.94)),
    delay(0.8, brewCode.show(extractRegion(clientSource, 'BrewEndpoint'), 1.6)),
    delay(2.8, brewCode.focus(2, 2)),
    delay(3, brewFocus(2, 2, 1.15, 2.6)),
  );
  heat.remove();
  yield* narrator.beat('live-brew-method',
    camera.reset(1),
    delay(0.3, brewCode.morph(extractRegion(machineSource, 'Brew'), 1.6, 'CoffeeMachine.cs')),
    delay(2.2, brewCode.focus(2, 5)),
    delay(4.2, brewCode.focus(8, 11)),
    delay(4.4, brewFocus(8, 11, 1.15, narrator.duration('live-brew-method') - 4.6)),
  );

  // A brew on the client, run by the server.
  const brew = new MachineWindows('brew');
  camera.add(brew);
  yield brew.server;
  yield brew.client;
  const click = brew.mark('click');
  const brewing = brew.mark('brewing');
  const pressure = brew.mark('pressure');
  const done = brew.mark('done');
  const clickDuration = narrator.duration('live-brew-click');
  yield* narrator.beat('live-brew-click',
    camera.reset(1),
    leave(brewCode, 0, -120, 0.7),
    delay(0.3, brew.arrive()),
    brew.play(clickDuration, {from: click - 2, to: brewing + 0.4}),
    delay(1.2, camera.focusOn(() => brew.pagePoint(brew.client, 560, 560), {zoom: 1.4, duration: clickDuration - 1.4, clear: brew.frames})),
  );
  brewCode.remove();
  const serverDuration = narrator.duration('live-brew-server');
  yield* narrator.beat('live-brew-server',
    brew.play(serverDuration, {from: brewing + 0.4, to: pressure + 2.5}),
    camera.focusOn(() => brew.pagePoint(brew.server, 400, 460), {zoom: 1.4, duration: 1.8, clear: brew.frames}),
    delay(2.2, camera.focusOn(() => brew.pagePoint(brew.server, 400, 360), {zoom: 1.25, duration: serverDuration - 2.4, clear: brew.frames})),
  );
  const doneDuration = narrator.duration('live-brew-done');
  yield* narrator.beat('live-brew-done',
    brew.play(doneDuration, {from: pressure + 2.5}),
    camera.reset(1.6),
    delay(doneDuration - 3.2, camera.focusOn(() => brew.pagePoint(brew.server, 400, 560).add(brew.pagePoint(brew.client, 400, 560)).scale(0.5), {zoom: 1.12, duration: 3, clear: brew.frames})),
  );

  // Both directions on the path.
  const back = new FlowDiagram({definition: livePath, opacity: 0});
  camera.add(back);
  yield* back.build();
  yield* narrator.beat('live-path-back',
    camera.reset(1),
    all(brew.opacity(0, 0.7, moveEasing), brew.scale(0.6, 0.8, moveEasing)),
    delay(0.4, all(back.opacity(1, 0.5), back.reveal(0), back.reveal(1), back.reveal(2), back.reveal(3))),
    delay(1.2, chain(
      back.pulse('simulator', 'machine', 0.4),
      back.pulse('machine', 'handler', 0.4),
      back.pulse('handler', 'source', 0.4),
      back.pulse('source', 'mirror', 0.4),
    )),
    delay(2.4, back.reveal(4)),
    delay(3.0, chain(back.pulse('mirror', 'source', 0.4), back.pulse('source', 'handler', 0.4), back.pulse('handler', 'machine', 0.4))),
  );
  brew.remove();
});
