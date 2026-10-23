import {Circle, Node} from '@revideo/2d';
import {all, createSignal, spring, transformVectorAsPoint, Vector2, type ThreadGenerator} from '@revideo/core';
import type {Arrow} from './components/Arrow';
import {palette, type AccentColor} from './palette';
import {arrivalSpring, enterEasing, moveEasing, smallShadow} from './style';

/** Fades a node in while it springs from `from` to full size. */
export function* arrive(node: Node, from = 0.9): ThreadGenerator {
  yield* all(node.opacity(1, 0.45, enterEasing), spring(arrivalSpring, from, 1, value => node.scale(value)));
}

/** Fades a node out while it drifts by the given offset. */
export function* leave(node: Node, offsetX = 0, offsetY = -40, duration = 0.6): ThreadGenerator {
  yield* all(node.opacity(0, duration, moveEasing), node.position(node.position().add([offsetX, offsetY]), duration, moveEasing));
}

/** A short nudge that draws the eye to a node without moving it. */
export function* nudge(node: Node, amount = 1.06): ThreadGenerator {
  yield* node.scale(amount, 0.18, enterEasing).to(1, 0.32, moveEasing);
}

/** A particle that travels a straight line between two points in `parent`'s space, then fades and is removed. */
export function* travel(parent: Node, from: Vector2, to: Vector2, color: AccentColor, duration = 0.9): ThreadGenerator {
  const progress = createSignal(0);
  const particle = new Circle({size: 18, fill: palette[color], opacity: 0, ...smallShadow, position: () => Vector2.lerp(from, to, progress())});
  parent.add(particle);
  yield* all(particle.opacity(1, 0.15), progress(1, duration, moveEasing));
  yield* particle.opacity(0, 0.2);
  particle.remove();
}

/**
 * Moves a node along an arrow's curve from one fraction of its length to another. The node and the arrow must
 * share a parent; the node keeps its final position afterwards.
 */
export function* ride(node: Node, arrow: Arrow, from: number, to: number, duration: number): ThreadGenerator {
  const progress = createSignal(from);
  node.position(() => arrow.line.getPointAtPercentage(progress()).position.add(arrow.position()));
  yield* progress(to, duration, moveEasing);
  node.position(node.position());
}

/** Converts a world point into the local space of a node, for placing callouts next to diagram nodes. */
export function toLocal(parent: Node, world: Vector2): Vector2 {
  return transformVectorAsPoint(world, parent.worldToLocal());
}
