import {Code, Rect, lines, makeScene2D} from '@revideo/2d';
import {createRef} from '@revideo/core';
import boilerSource from '../../../domain/Coffee/Boiler.cs?raw';
import {csharp} from '../../../theme/csharp';
import {Narrator} from '../../../theme/narrator';
import {palette} from '../../../theme/palette';
import {extractRegion} from '../../../theme/regions';
import {useTiming} from '../../../theme/variables';

export default makeScene2D('main', function* (view) {
  const narrator = new Narrator(view, useTiming());
  const code = createRef<Code>();

  view.add(
    <Rect layout fill={palette.mantle} stroke={palette.surface} lineWidth={2} radius={16} padding={40}>
      <Code ref={code} highlighter={csharp} fontSize={34} fontFamily={'JetBrains Mono, monospace'} code={''} />
    </Rect>,
  );

  yield* narrator.beat('boiler-class', code().code(extractRegion(boilerSource, 'Boiler'), 1.5));
  yield* narrator.beat('boiler-derived', code().selection(lines(10, 11), 0.6));
  yield* narrator.beat('boiler-pause');
});
