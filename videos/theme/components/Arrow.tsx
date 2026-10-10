import {CubicBezier, Line, Node, type NodeProps} from '@revideo/2d';
import {createRef, type ThreadGenerator} from '@revideo/core';
import type {CubicCurve} from '../geometry';
import {palette} from '../palette';
import {moveEasing} from '../style';

export interface ArrowProps extends NodeProps {
  curve: CubicCurve;
  color?: string;
  lineWidth?: number;
  dashed?: boolean;
}

/** Size of the rounded arrowhead. */
const headSize = 13;

/** Smooth curve with rounded caps and a small rounded chevron that rides on the drawing tip. */
export class Arrow extends Node {
  private readonly lineReference = createRef<CubicBezier>();

  public constructor(props: ArrowProps) {
    const {curve, color, lineWidth, dashed, ...rest} = props;
    super(rest);
    const stroke = color ?? palette.edge;
    const width = lineWidth ?? 4;
    this.add(
      <CubicBezier ref={this.lineReference} p0={[curve.p0.x, curve.p0.y]} p1={[curve.p1.x, curve.p1.y]}
        p2={[curve.p2.x, curve.p2.y]} p3={[curve.p3.x, curve.p3.y]} stroke={stroke} lineWidth={width}
        lineCap={'round'} lineDash={dashed ? [10, 14] : []} end={0} />,
    );
    const line = this.lineReference();
    this.add(
      <Line points={[[-headSize, -headSize * 0.75], [0, 0], [-headSize, headSize * 0.75]]} stroke={stroke} lineWidth={width}
        lineCap={'round'} lineJoin={'round'} opacity={() => (line.end() > 0.04 ? 1 : 0)}
        position={() => line.getPointAtPercentage(line.end()).position}
        rotation={() => line.getPointAtPercentage(line.end()).tangent.degrees} />,
    );
  }

  public get line(): CubicBezier {
    return this.lineReference();
  }

  public *grow(duration = 0.7): ThreadGenerator {
    yield* this.line.end(1, duration, moveEasing);
  }
}

