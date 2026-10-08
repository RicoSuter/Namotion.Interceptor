import {Node, type NodeProps} from '@revideo/2d';
import {all, transformVectorAsPoint, Vector2, type ThreadGenerator} from '@revideo/core';
import {durations, moveEasing} from '../style';

export interface FocusOptions {
  zoom?: number;
  duration?: number;
}

/** Wraps scene content and moves over it with eased zoom and pan. */
export class Camera extends Node {
  public constructor(props: NodeProps) {
    super(props);
  }

  /** Centers the target node, or a point in world coordinates, and zooms in on it. */
  public *focusOn(target: Node | Vector2, options: FocusOptions = {}): ThreadGenerator {
    const zoom = options.zoom ?? 1.4;
    const duration = options.duration ?? durations.slow;
    const world = target instanceof Vector2 ? target : target.absolutePosition();
    const local = transformVectorAsPoint(world, this.worldToLocal());
    yield* all(this.scale(zoom, duration, moveEasing), this.position(local.scale(-zoom), duration, moveEasing));
  }

  /** Returns to the full view. */
  public *reset(duration: number = durations.slow): ThreadGenerator {
    yield* all(this.scale(1, duration, moveEasing), this.position(0, duration, moveEasing));
  }
}
