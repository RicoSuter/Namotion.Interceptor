import {Circle, Node, Rect, Txt, type NodeProps} from '@revideo/2d';
import {all, createRef, createSignal, delay, sequence, spring, type ThreadGenerator} from '@revideo/core';
import {edgeStep, layoutFlow, type FlowDefinition, type FlowLayout} from '../flowLayout';
import {palette} from '../palette';
import {arrivalSpring, enterEasing, fonts, fontSize, moveEasing} from '../style';
import {Arrow} from './Arrow';
import {Card} from './Card';

export interface FlowDiagramProps extends NodeProps {
  definition: FlowDefinition;
}

/** Nodes and curved edges from a typed definition, laid out with elkjs and revealed step by step. */
export class FlowDiagram extends Node {
  private readonly definition: FlowDefinition;
  private readonly nodes = new Map<string, Rect>();
  private readonly texts = new Map<string, {label: Txt; detail: Txt | null}>();
  private readonly arrows: Arrow[] = [];

  public constructor(props: FlowDiagramProps) {
    const {definition, ...rest} = props;
    super(rest);
    this.definition = definition;
  }

  /** Computes the layout and creates the hidden nodes and edges. Run once before revealing. */
  public *build(): ThreadGenerator {
    // The scene runner resolves a yielded promise and passes its value back in.
    const flowLayout: FlowLayout = yield layoutFlow(this.definition);

    const edgeLayer = new Node({});
    this.add(edgeLayer);
    this.definition.edges.forEach((edge, index) => {
      const arrow = new Arrow({curve: flowLayout.curves[index]});
      if (edge.label) {
        const middle = arrow.line.getPointAtPercentage(0.5).position;
        arrow.add(
          <Rect layout x={middle.x} y={middle.y} padding={[6, 14]} radius={999} fill={palette.background}
            opacity={() => (arrow.line.end() > 0.5 ? 1 : 0)}>
            <Txt fontFamily={fonts.text} fontWeight={500} fontSize={20} fill={palette.secondaryText} text={edge.label} />
          </Rect>,
        );
      }
      this.arrows.push(arrow);
      edgeLayer.add(arrow);
    });

    for (const definition of this.definition.nodes) {
      const box = flowLayout.boxes.get(definition.id)!;
      const accent = palette[definition.color ?? 'blue'];
      const label = createRef<Txt>();
      const detail = createRef<Txt>();
      const node = (
        <Card radius={20} layout direction={'column'} justifyContent={'center'} alignItems={'center'}
          gap={6} x={box.x} y={box.y} width={box.width} height={box.height} opacity={0} scale={0.86}>
          <Rect layout alignItems={'center'} gap={12}>
            <Circle size={12} fill={accent} />
            <Txt ref={label} fontFamily={fonts.text} fontWeight={600} fontSize={fontSize.label} fill={palette.text} text={definition.label} />
          </Rect>
          {definition.detail ? (
            <Txt ref={detail} fontFamily={fonts.text} fontSize={fontSize.detail} fill={palette.secondaryText} text={definition.detail} />
          ) : null}
        </Card>
      ) as Card;
      this.nodes.set(definition.id, node);
      this.texts.set(definition.id, {label: label(), detail: definition.detail ? detail() : null});
      this.add(node);
    }
  }

  /** Every node, revealed or not, for keeping them clear of the chapter header in camera moves. */
  public get boxes(): Rect[] {
    return [...this.nodes.values()];
  }

  /** The node with the given id, for example as a camera target. */
  public node(id: string): Rect {
    const node = this.nodes.get(id);
    if (!node) {
      throw new Error(`Flow diagram has no node '${id}'`);
    }
    return node;
  }

  /**
   * Replaces a node's label or detail with a short cross fade, for example when a node takes a new role.
   * The node keeps its size, so the new text should be about as long as the old one.
   */
  public *retext(id: string, text: {label?: string; detail?: string}, duration = 0.6): ThreadGenerator {
    this.node(id);
    const texts = this.texts.get(id)!;
    const changes: Array<[Txt, string]> = [];
    if (text.label !== undefined) {
      changes.push([texts.label, text.label]);
    }
    if (text.detail !== undefined) {
      if (!texts.detail) {
        throw new Error(`Flow diagram node '${id}' has no detail line to replace`);
      }
      changes.push([texts.detail, text.detail]);
    }
    yield* all(...changes.map(([node]) => node.opacity(0, duration / 2, moveEasing)));
    for (const [node, value] of changes) {
      node.text(value);
    }
    yield* all(...changes.map(([node]) => node.opacity(1, duration / 2, enterEasing)));
  }

  /** Brings in the nodes of a step with a spring, then draws the edges of that step. */
  public *reveal(step: number): ThreadGenerator {
    const nodes = this.definition.nodes.filter(node => (node.step ?? 0) === step).map(node => this.node(node.id));
    const arrows = this.definition.edges.map((edge, index) => ({edge, arrow: this.arrows[index]}))
      .filter(({edge}) => edgeStep(this.definition, edge) === step);
    yield* all(
      sequence(0.1, ...nodes.map(node => arrive(node))),
      delay(nodes.length > 0 ? 0.25 : 0, sequence(0.12, ...arrows.map(({arrow}) => arrow.grow(0.65)))),
    );
  }

  /** Sends a particle along the edge between two nodes and nudges the target when it arrives. */
  public *pulse(from: string, to: string, duration = 1): ThreadGenerator {
    const index = this.definition.edges.findIndex(edge => edge.from === from && edge.to === to);
    if (index < 0) {
      throw new Error(`Flow diagram has no edge from '${from}' to '${to}'`);
    }
    const line = this.arrows[index].line;
    const target = this.node(to);
    const color = palette[this.definition.nodes.find(node => node.id === to)?.color ?? 'blue'];
    const progress = createSignal(0);
    const particle = (
      <Circle size={16} fill={color} opacity={0} shadowColor={'rgba(0, 0, 0, 0.5)'} shadowBlur={8}
        position={() => line.getPointAtPercentage(progress()).position} />
    ) as Circle;
    this.add(particle);
    yield* all(particle.opacity(1, 0.15), progress(1, duration, moveEasing));
    yield* all(particle.opacity(0, 0.2), target.scale(1.06, 0.18, enterEasing).to(1, 0.3, moveEasing));
    particle.remove();
  }
}

function* arrive(node: Rect): ThreadGenerator {
  yield* all(node.opacity(1, 0.35, enterEasing), spring(arrivalSpring, 0.86, 1, value => node.scale(value)));
}
