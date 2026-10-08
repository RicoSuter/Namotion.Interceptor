import ELK, {type ElkNode} from 'elkjs/lib/elk.bundled.js';
import {routeEdges, type Box, type CubicCurve, type Point} from './geometry';
import type {AccentColor} from './palette';

export interface FlowNodeDefinition {
  id: string;
  label: string;
  /** Second, smaller line under the label. */
  detail?: string;
  color?: AccentColor;
  /** Reveal step the node appears in. Defaults to 0. */
  step?: number;
  width?: number;
  height?: number;
  /** Fixed center that replaces the computed position. */
  position?: Point;
}

export interface FlowEdgeDefinition {
  from: string;
  to: string;
  label?: string;
  /** Reveal step the edge is drawn in. Defaults to the later step of its two nodes. */
  step?: number;
}

export interface FlowDefinition {
  direction?: 'right' | 'down';
  nodes: FlowNodeDefinition[];
  edges: FlowEdgeDefinition[];
  /** Gap between nodes in the same layer. */
  nodeSpacing?: number;
  /** Gap between layers. */
  layerSpacing?: number;
}

export interface FlowLayout {
  /** Node boxes centered around the origin. */
  boxes: Map<string, Box>;
  curves: CubicCurve[];
  width: number;
  height: number;
}

export const defaultNodeWidth = 300;

export function nodeSize(node: FlowNodeDefinition): {width: number; height: number} {
  return {width: node.width ?? defaultNodeWidth, height: node.height ?? (node.detail ? 124 : 96)};
}

/** Lays the nodes out in layers with elkjs and routes the edges as smooth curves between them. */
export async function layoutFlow(definition: FlowDefinition): Promise<FlowLayout> {
  const elk = new ELK();
  const input: ElkNode = {
    id: 'root',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': definition.direction === 'down' ? 'DOWN' : 'RIGHT',
      'elk.spacing.nodeNode': String(definition.nodeSpacing ?? 72),
      'elk.layered.spacing.nodeNodeBetweenLayers': String(definition.layerSpacing ?? 150),
      'elk.layered.nodePlacement.strategy': 'BRANDES_KOEPF',
      'elk.layered.nodePlacement.bk.fixedAlignment': 'BALANCED',
    },
    children: definition.nodes.map(node => ({id: node.id, ...nodeSize(node)})),
    edges: definition.edges.map((edge, index) => ({id: `edge-${index}`, sources: [edge.from], targets: [edge.to]})),
  };
  const graph = await elk.layout(input);

  const width = graph.width ?? 0;
  const height = graph.height ?? 0;
  const boxes = new Map<string, Box>();
  for (const child of graph.children ?? []) {
    const node = definition.nodes.find(candidate => candidate.id === child.id)!;
    const size = nodeSize(node);
    const center = node.position ?? {x: (child.x ?? 0) + size.width / 2 - width / 2, y: (child.y ?? 0) + size.height / 2 - height / 2};
    boxes.set(child.id, {...center, ...size});
  }
  return {boxes, curves: routeEdges(boxes, definition.edges), width, height};
}

/** The step an edge is drawn in: its own, or the later step of its two nodes. */
export function edgeStep(definition: FlowDefinition, edge: FlowEdgeDefinition): number {
  if (edge.step !== undefined) {
    return edge.step;
  }
  const step = (id: string) => definition.nodes.find(node => node.id === id)?.step ?? 0;
  return Math.max(step(edge.from), step(edge.to));
}
