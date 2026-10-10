import {describe, expect, it} from 'vitest';
import {edgeStep, layoutFlow, type FlowDefinition} from './flowLayout';

const chain: FlowDefinition = {
  nodes: [
    {id: 'simulator', label: 'Simulator'},
    {id: 'boiler', label: 'Boiler', step: 1},
    {id: 'machine', label: 'Machine', step: 2},
  ],
  edges: [
    {from: 'simulator', to: 'boiler'},
    {from: 'boiler', to: 'machine', step: 3},
  ],
};

describe('layoutFlow', () => {
  it('WhenDirectionIsRight_ThenNodesFollowEachOtherLeftToRightAroundTheOrigin', async () => {
    // Act
    const layout = await layoutFlow(chain);

    // Assert
    const simulator = layout.boxes.get('simulator')!;
    const boiler = layout.boxes.get('boiler')!;
    const machine = layout.boxes.get('machine')!;
    expect(simulator.x).toBeLessThan(boiler.x);
    expect(boiler.x).toBeLessThan(machine.x);
    expect(simulator.x + machine.x).toBeCloseTo(0);
  });

  it('WhenATreeIsLaidOutTopDown_ThenChildrenKeepTheirDefinitionOrder', async () => {
    // Arrange
    const children = ['boiler', 'pump', 'tank', 'hopper'];
    const tree: FlowDefinition = {
      direction: 'down',
      nodes: [{id: 'machine', label: 'Machine'}, ...children.map(id => ({id, label: id}))],
      edges: children.map(id => ({from: 'machine', to: id})),
    };

    // Act
    const layout = await layoutFlow(tree);

    // Assert
    const xs = children.map(id => layout.boxes.get(id)!.x);
    expect(xs).toEqual([...xs].sort((left, right) => left - right));
  });

  it('WhenNodesAreLaidOut_ThenEdgesStartAndEndOnNodeSides', async () => {
    // Act
    const layout = await layoutFlow(chain);

    // Assert
    const simulator = layout.boxes.get('simulator')!;
    const boiler = layout.boxes.get('boiler')!;
    expect(layout.curves[0].p0.x).toBeCloseTo(simulator.x + simulator.width / 2);
    expect(layout.curves[0].p3.x).toBeCloseTo(boiler.x - boiler.width / 2);
  });

  it('WhenNodeHasPosition_ThenPositionOverridesLayout', async () => {
    // Arrange
    const definition: FlowDefinition = {...chain, nodes: chain.nodes.map(node => (node.id === 'boiler' ? {...node, position: {x: 10, y: 400}} : node))};

    // Act
    const layout = await layoutFlow(definition);

    // Assert
    expect(layout.boxes.get('boiler')).toMatchObject({x: 10, y: 400});
  });
});

describe('edgeStep', () => {
  it('WhenEdgeHasNoStep_ThenItUsesTheLaterNodeStep', () => {
    // Act & Assert
    expect(edgeStep(chain, chain.edges[0])).toBe(1);
  });

  it('WhenEdgeHasStep_ThenItIsUsed', () => {
    // Act & Assert
    expect(edgeStep(chain, chain.edges[1])).toBe(3);
  });
});
