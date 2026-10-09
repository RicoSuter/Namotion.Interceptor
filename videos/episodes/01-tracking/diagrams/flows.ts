import type {FlowDefinition} from '../../../theme/flowLayout';

// Concept colors, used the same way in every diagram of the episode:
// coffee machine and its recipes purple, boiler pink, pump blue, water tank cyan, bean hopper green,
// simulator orange; change channels blue, cyan and green.

/** The coffee machine as a tree of tracked objects. */
export const machineTree: FlowDefinition = {
  direction: 'down',
  nodeSpacing: 40,
  layerSpacing: 130,
  nodes: [
    {id: 'machine', label: 'Coffee machine', detail: 'State, Status, IsReady', color: 'purple', width: 340},
    {id: 'boiler', label: 'Boiler', detail: 'Temperature', color: 'pink', step: 1, width: 260},
    {id: 'pump', label: 'Pump', detail: 'Pressure', color: 'blue', step: 1, width: 260},
    {id: 'tank', label: 'Water tank', detail: 'Level', color: 'cyan', step: 1, width: 260},
    {id: 'hopper', label: 'Bean hopper', detail: 'GrindSize', color: 'green', step: 1, width: 260},
    {id: 'recipes', label: 'Recipes', detail: 'Espresso, Lungo', color: 'purple', step: 1, width: 260},
  ],
  edges: [
    {from: 'machine', to: 'boiler'},
    {from: 'machine', to: 'pump'},
    {from: 'machine', to: 'tank'},
    {from: 'machine', to: 'hopper'},
    {from: 'machine', to: 'recipes'},
  ],
};

/** The simulator, a separate one-node diagram placed beside the tree. */
export const simulator: FlowDefinition = {
  nodes: [{id: 'simulator', label: 'Simulator', detail: 'every 100 ms', color: 'orange', width: 260}],
  edges: [],
};

/** What IsReady and Status read, flattened to the intercepted properties, with the derived IsHot and IsLow between. */
export const dependencies: FlowDefinition = {
  direction: 'right',
  nodeSpacing: 36,
  layerSpacing: 150,
  nodes: [
    {id: 'temperature', label: 'Temperature', detail: 'Boiler', color: 'pink', step: 1, width: 290},
    {id: 'target', label: 'TargetTemperature', detail: 'Boiler', color: 'pink', step: 1, width: 290},
    {id: 'state', label: 'State', detail: 'CoffeeMachine', color: 'purple', width: 290},
    {id: 'level', label: 'Level', detail: 'WaterTank', color: 'cyan', step: 1, width: 290},
    {id: 'hot', label: 'IsHot', detail: 'derived', color: 'pink', width: 250},
    {id: 'low', label: 'IsLow', detail: 'derived', color: 'cyan', width: 250},
    {id: 'ready', label: 'IsReady', detail: 'derived', color: 'purple', width: 250},
    {id: 'status', label: 'Status', detail: 'derived', color: 'purple', step: 2, width: 250},
  ],
  edges: [
    {from: 'temperature', to: 'hot'},
    {from: 'target', to: 'hot'},
    {from: 'level', to: 'low'},
    {from: 'state', to: 'ready'},
    {from: 'hot', to: 'ready'},
    {from: 'low', to: 'ready'},
    {from: 'hot', to: 'status'},
    {from: 'low', to: 'status'},
    {from: 'temperature', to: 'status'},
  ],
};

/** One write, published through three channels. */
export const channels: FlowDefinition = {
  direction: 'right',
  nodeSpacing: 44,
  layerSpacing: 200,
  nodes: [
    {id: 'write', label: 'Property write', detail: 'one change', color: 'purple', width: 300},
    {id: 'observable', label: 'Observable', detail: 'Rx, on a scheduler', color: 'blue', step: 1, width: 320},
    {id: 'queue', label: 'Queue', detail: 'one consumer thread', color: 'cyan', step: 1, width: 320},
    {id: 'property', label: 'Single property', detail: 'one subject', color: 'green', step: 1, width: 320},
  ],
  edges: [
    {from: 'write', to: 'observable'},
    {from: 'write', to: 'queue'},
    {from: 'write', to: 'property'},
  ],
};

/** The recipes branch of the graph, where the Ristretto is attached. */
export const recipeTree: FlowDefinition = {
  direction: 'down',
  nodeSpacing: 44,
  layerSpacing: 120,
  nodes: [
    {id: 'machine', label: 'Coffee machine', detail: 'context', color: 'purple', width: 320},
    {id: 'boiler', label: 'Boiler', color: 'pink', width: 240},
    {id: 'recipes', label: 'Recipes', detail: 'dictionary', color: 'purple', width: 260},
    {id: 'pump', label: 'Pump', color: 'blue', width: 240},
    {id: 'espresso', label: 'Espresso', detail: '40 ml, 93 °C', color: 'purple', width: 260},
    {id: 'lungo', label: 'Lungo', detail: '110 ml, 92 °C', color: 'purple', width: 260},
    {id: 'ristretto', label: 'Ristretto', detail: '25 ml, 97 °C', color: 'purple', step: 1, width: 260},
  ],
  edges: [
    {from: 'machine', to: 'boiler'},
    {from: 'machine', to: 'recipes'},
    {from: 'machine', to: 'pump'},
    {from: 'recipes', to: 'espresso'},
    {from: 'recipes', to: 'lungo'},
    {from: 'recipes', to: 'ristretto'},
  ],
};
