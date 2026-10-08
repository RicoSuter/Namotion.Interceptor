import {Layout, Node, type NodeProps} from '@revideo/2d';
import {all, transformVectorAsPoint, Vector2, type ThreadGenerator, type Vector2Signal} from '@revideo/core';
import {clearOfHeader} from '../geometry';
import {durations, enterEasing, moveEasing} from '../style';

export interface FocusOptions {
  zoom?: number;
  duration?: number;
  /**
   * Content to keep clear of the chapter header in the top right corner: when the move would push its top edge
   * under the header, the camera centers a little higher on the content instead.
   */
  clear?: Layout | Layout[];
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
    yield* this.glide(transformVectorAsPoint(world, this.worldToLocal()), zoom, duration, options.clear);
  }

  /**
   * Centers a point given in the camera's content coordinates, the coordinates its children use (origin at
   * the frame center), and zooms in on it. focusOn takes world coordinates instead, whose origin is the top
   * left corner of the frame.
   */
  public *focusOnPoint(point: Vector2, options: FocusOptions = {}): ThreadGenerator {
    yield* this.glide(point, options.zoom ?? 1.4, options.duration ?? durations.slow, options.clear);
  }

  /** Returns to the full view. */
  public *reset(duration: number = durations.slow): ThreadGenerator {
    yield* all(this.scale(1, duration, moveEasing), this.position(0, duration, moveEasing), this.lead(Vector2.zero, duration));
  }

  private *glide(point: Vector2, zoom: number, duration: number, clear: Layout | Layout[] | undefined): ThreadGenerator {
    let position = point.scale(-zoom);
    if (clear) {
      const corners = [clear].flat().flatMap(node => {
        const half = node.size().scale(0.5);
        const toLocal = this.worldToLocal().multiply(node.localToWorld());
        return [new Vector2(-half.x, -half.y), new Vector2(half.x, -half.y), new Vector2(half.x, half.y)]
          .map(corner => transformVectorAsPoint(corner, toLocal));
      });
      const top = Math.min(...corners.map(corner => corner.y));
      const right = Math.max(...corners.map(corner => corner.x));
      position = new Vector2(clearOfHeader(top, right, zoom, position));
    }
    const centered = position.scale(-1 / zoom);
    yield* all(this.scale(zoom, duration, moveEasing), this.position(position, duration, moveEasing), this.lead(centered, duration));
  }

  private *lead(point: Vector2, duration: number): ThreadGenerator {
    yield* this.focus(point, duration * 0.6, enterEasing);
  }
}
