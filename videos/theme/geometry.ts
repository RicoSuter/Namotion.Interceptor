export interface Point {
  x: number;
  y: number;
}

/** A rectangle given by its center and size. */
export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface CubicCurve {
  p0: Point;
  p1: Point;
  p2: Point;
  p3: Point;
}

export type Side = 'left' | 'right' | 'top' | 'bottom';

export interface EdgeEnds {
  from: string;
  to: string;
}

/** Shortest handle length so short edges still leave and enter their nodes perpendicular. */
const minimumHandle = 48;

/** Share of a node side used to spread several edge anchors. */
const anchorSpread = 0.6;

const outward: Record<Side, Point> = {left: {x: -1, y: 0}, right: {x: 1, y: 0}, top: {x: 0, y: -1}, bottom: {x: 0, y: 1}};

/**
 * Picks the sides an edge leaves and enters by, using the axis with the larger gap between the boxes. In a
 * top-down layout, boxes in different layers always connect vertically, so a tree's outer branches do not
 * enter their children from the side.
 */
export function connectionSides(source: Box, target: Box, direction: 'right' | 'down' = 'right'): {source: Side; target: Side} {
  const gapX = Math.abs(target.x - source.x) - (source.width + target.width) / 2;
  const gapY = Math.abs(target.y - source.y) - (source.height + target.height) / 2;
  if (direction === 'down' && gapY > 0) {
    return target.y >= source.y ? {source: 'bottom', target: 'top'} : {source: 'top', target: 'bottom'};
  }
  if (gapX >= gapY) {
    return target.x >= source.x ? {source: 'right', target: 'left'} : {source: 'left', target: 'right'};
  }
  return target.y >= source.y ? {source: 'bottom', target: 'top'} : {source: 'top', target: 'bottom'};
}

/** A smooth curve that leaves and enters perpendicular to the given sides. */
export function sideCurve(start: Point, startSide: Side, end: Point, endSide: Side): CubicCurve {
  const startDirection = outward[startSide];
  const endDirection = outward[endSide];
  const horizontal = startDirection.x !== 0;
  const distance = horizontal ? Math.abs(end.x - start.x) : Math.abs(end.y - start.y);
  const handle = Math.max(distance * 0.5, minimumHandle);
  return {
    p0: start,
    p1: {x: start.x + startDirection.x * handle, y: start.y + startDirection.y * handle},
    p2: {x: end.x + endDirection.x * handle, y: end.y + endDirection.y * handle},
    p3: end,
  };
}

/**
 * Routes every edge as a cubic curve between node sides. Edges that share a side get anchors spread
 * along it, ordered by the position of their other end so the curves do not cross near the node.
 */
export function routeEdges(boxes: ReadonlyMap<string, Box>, edges: readonly EdgeEnds[], direction: 'right' | 'down' = 'right'): CubicCurve[] {
  const box = (id: string) => {
    const found = boxes.get(id);
    if (!found) {
      throw new Error(`Edge refers to unknown node '${id}'`);
    }
    return found;
  };
  const sides = edges.map(edge => connectionSides(box(edge.from), box(edge.to), direction));

  interface AnchorRequest {
    edge: number;
    end: 'source' | 'target';
    other: Box;
  }
  const groups = new Map<string, AnchorRequest[]>();
  edges.forEach((edge, index) => {
    const add = (node: string, side: Side, request: AnchorRequest) => {
      const key = `${node}:${side}`;
      groups.set(key, [...(groups.get(key) ?? []), request]);
    };
    add(edge.from, sides[index].source, {edge: index, end: 'source', other: box(edge.to)});
    add(edge.to, sides[index].target, {edge: index, end: 'target', other: box(edge.from)});
  });

  const anchors = edges.map(() => ({source: {x: 0, y: 0}, target: {x: 0, y: 0}}));
  for (const [key, requests] of groups) {
    const separator = key.lastIndexOf(':');
    const node = box(key.slice(0, separator));
    const side = key.slice(separator + 1) as Side;
    const alongX = side === 'top' || side === 'bottom';
    const sorted = [...requests].sort((a, b) => (alongX ? a.other.x - b.other.x : a.other.y - b.other.y));
    sorted.forEach((request, order) => {
      const fraction = sorted.length === 1 ? 0 : (order / (sorted.length - 1) - 0.5) * anchorSpread;
      anchors[request.edge][request.end] = sidePoint(node, side, fraction);
    });
  }

  return edges.map((_, index) => sideCurve(anchors[index].source, sides[index].source, anchors[index].target, sides[index].target));
}

/** A point on a box side; fraction runs from -0.5 to 0.5 along the side, 0 is the middle. */
export function sidePoint(box: Box, side: Side, fraction = 0): Point {
  switch (side) {
    case 'left':
      return {x: box.x - box.width / 2, y: box.y + fraction * box.height};
    case 'right':
      return {x: box.x + box.width / 2, y: box.y + fraction * box.height};
    case 'top':
      return {x: box.x + fraction * box.width, y: box.y - box.height / 2};
    case 'bottom':
      return {x: box.x + fraction * box.width, y: box.y + box.height / 2};
  }
}

/** A gently bowed arrow between two lifelines, or a loop to the right when both ends are the same lifeline. */
export function messageCurve(fromX: number, toX: number, y: number): CubicCurve {
  if (fromX === toX) {
    const reach = 110;
    return {p0: {x: fromX, y: y - 22}, p1: {x: fromX + reach, y: y - 40}, p2: {x: fromX + reach, y: y + 40}, p3: {x: fromX, y: y + 22}};
  }
  const width = toX - fromX;
  const bow = Math.min(36, Math.abs(width) * 0.1);
  return {
    p0: {x: fromX, y},
    p1: {x: fromX + width * 0.3, y: y - bow},
    p2: {x: fromX + width * 0.7, y: y - bow},
    p3: {x: toX, y},
  };
}

/**
 * Scroll offset that keeps a focused range visible: unchanged when it already fits in the viewport,
 * otherwise centered on the range and clamped to the content.
 */
export function scrollToFocus(viewportHeight: number, contentHeight: number, focusTop: number, focusBottom: number, currentScroll = 0): number {
  const maximum = Math.max(contentHeight - viewportHeight, 0);
  if (focusTop >= currentScroll && focusBottom <= currentScroll + viewportHeight) {
    return Math.min(currentScroll, maximum);
  }
  const centered = (focusTop + focusBottom) / 2 - viewportHeight / 2;
  return Math.min(Math.max(centered, 0), maximum);
}

/** The chapter header's region of the frame with its clearance, in view coordinates (origin at the frame center), reaching up to the top edge: content keeps out of it. */
export interface HeaderZone {
  left: number;
  right: number;
  bottom: number;
}

/** Left and right edges and the top edge of one content node, in the camera's content coordinates. */
export interface ContentEdges {
  left: number;
  right: number;
  top: number;
}

/**
 * Camera position that keeps content clear of the chapter header: when a content node, given in the camera's
 * content coordinates, would reach into the header zone at this zoom, the position moves down just enough for the
 * node reaching in furthest; otherwise it is returned unchanged.
 */
export function clearOfHeader(contents: readonly ContentEdges[], zoom: number, position: Point, zone: HeaderZone): Point {
  let shift = 0;
  for (const content of contents) {
    const left = content.left * zoom + position.x;
    const right = content.right * zoom + position.x;
    const top = content.top * zoom + position.y;
    if (left < zone.right && right > zone.left && top < zone.bottom) {
      shift = Math.max(shift, zone.bottom - top);
    }
  }
  return shift === 0 ? position : {x: position.x, y: position.y + shift};
}
