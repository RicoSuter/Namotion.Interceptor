import {Node, type NodeProps} from '@revideo/2d';
import {all, transformVectorAsPoint, Vector2, type ThreadGenerator, type Vector2Signal} from '@revideo/core';
import {durations, enterEasing, moveEasing} from '../style';

export interface FocusOptions {
  zoom?: number;
  duration?: number;
}

/** Wraps scene content and moves over it with eased zoom and pan. */
export class Camera extends Node {
  /**
   * The point being focused, in content coordinates. It reaches a new target ahead of the camera, so a background
   * light that follows it leads the eye toward the subject.
   */
  public readonly focus: Vector2Signal<void> = Vector2.createSignal(Vector2.zero);

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
    yield* all(this.scale(zoom, duration, moveEasing), this.position(local.scale(-zoom), duration, moveEasing), this.lead(local, duration));
  }

  /**
   * Centers a point given in the camera's content coordinates, the coordinates its children use (origin at
   * the frame center), and zooms in on it. focusOn takes world coordinates instead, whose origin is the top
   * left corner of the frame.
   */
  public *focusOnPoint(point: Vector2, options: FocusOptions = {}): ThreadGenerator {
    const zoom = options.zoom ?? 1.4;
    const duration = options.duration ?? durations.slow;
    yield* all(this.scale(zoom, duration, moveEasing), this.position(point.scale(-zoom), duration, moveEasing), this.lead(point, duration));
  }

  /** Returns to the full view. */
  public *reset(duration: number = durations.slow): ThreadGenerator {
    yield* all(this.scale(1, duration, moveEasing), this.position(0, duration, moveEasing), this.lead(Vector2.zero, duration));
  }

  private *lead(point: Vector2, duration: number): ThreadGenerator {
    yield* this.focus(point, duration * 0.6, enterEasing);
  }
}
