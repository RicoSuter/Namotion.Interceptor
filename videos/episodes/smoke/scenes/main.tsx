import {Code, Rect, Video, lines, makeScene2D} from '@revideo/2d';
import {all, createRef} from '@revideo/core';
import boilerSource from '../../../domain/Coffee/Boiler.cs?raw';
import {csharp} from '../../../theme/csharp';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {clipUrl, useTiming} from '../../../theme/variables';

export default makeScene2D('main', function* (view) {
  const narrator = new Narrator(view, useTiming());
  const card = createRef<Rect>();
  const code = createRef<Code>();
  const video = createRef<Video>();

  view.add(
    <Rect ref={card} layout fill={palette.mantle} stroke={palette.surface} lineWidth={2} radius={16} padding={40}>
      <Code ref={code} highlighter={csharp} fontSize={34} fontFamily={'JetBrains Mono, monospace'} code={''} />
    </Rect>,
  );
  view.add(<Video ref={video} src={clipUrl('status')} x={500} width={800} opacity={0} />);

  yield* narrator.beat('boiler-class', code().code(extractRegion(boilerSource, 'Boiler'), 1.5));
  yield* narrator.beat('boiler-derived', code().selection(lines(10, 11), 0.6));
  yield* narrator.beat('boiler-pause');

  video().play();
  yield* narrator.beat('boiler-live', all(card().x(-440, 0.8), card().scale(0.75, 0.8), video().opacity(1, 0.6)));
});
