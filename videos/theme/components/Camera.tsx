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

  /**
   * Centers the target and zooms in on it. The target is a node or a point in world coordinates; pass a
   * function to compute the point when the move starts, after earlier camera moves.
   */
  public *focusOn(target: Node | Vector2 | (() => Vector2), options: FocusOptions = {}): ThreadGenerator {
    const zoom = options.zoom ?? 1.4;
    const duration = options.duration ?? durations.slow;
    const world = target instanceof Node ? target.absolutePosition() : target instanceof Vector2 ? target : target();
    const local = transformVectorAsPoint(world, this.worldToLocal());
    yield* all(this.scale(zoom, duration, moveEasing), this.position(local.scale(-zoom), duration, moveEasing));
  }

  /** Returns to the full view. */
  public *reset(duration: number = durations.slow): ThreadGenerator {
    yield* all(this.scale(1, duration, moveEasing), this.position(0, duration, moveEasing));
  }
}
