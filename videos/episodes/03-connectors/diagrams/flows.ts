import type {FlowDefinition} from '../../../theme/flowLayout';

// Concept colors, used the same way in every diagram of the episode:
// machine purple, boiler pink, pump blue, water tank cyan, bean hopper green, simulator orange,
// external system orange, source blue, server and handler purple-blue, client cyan.

/** The coffee machine as a tree of tracked objects, with the simulator that writes to it. */
export const machineTree: FlowDefinition = {
  direction: 'down',
  nodeSpacing: 48,
  layerSpacing: 130,
  nodes: [
    {id: 'machine', label: 'Coffee machine', detail: 'State, IsReady', color: 'purple', width: 320},
    {id: 'boiler', label: 'Boiler', detail: 'Temperature', color: 'pink', step: 1},
    {id: 'pump', label: 'Pump', detail: 'Pressure', color: 'blue', step: 1},
    {id: 'tank', label: 'Water tank', detail: 'Level', color: 'cyan', step: 1},
    {id: 'hopper', label: 'Bean hopper', detail: 'GrindSize', color: 'green', step: 1},
  ],
  edges: [
    {from: 'machine', to: 'boiler'},
    {from: 'machine', to: 'pump'},
    {from: 'machine', to: 'tank'},
    {from: 'machine', to: 'hopper'},
  ],
};

/** A connector between the model and an external system, with values flowing both ways. */
export const bridge: FlowDefinition = {
  direction: 'right',
  layerSpacing: 170,
  nodes: [
    {id: 'model', label: 'Your model', detail: 'subject tree', color: 'purple'},
    {id: 'connector', label: 'Connector', detail: 'OPC UA, MQTT, WebSocket', color: 'blue', step: 1, width: 340},
    {id: 'external', label: 'External system', detail: 'controller, broker, app', color: 'orange', step: 1, width: 320},
  ],
  edges: [
    {from: 'model', to: 'connector'},
    {from: 'connector', to: 'external'},
    {from: 'external', to: 'connector'},
    {from: 'connector', to: 'model'},
  ],
};

/** A source: the external system owns the data and the local model is its replica. */
export const sourceRow: FlowDefinition = {
  direction: 'right',
  layerSpacing: 150,
  nodes: [
    {id: 'owner', label: 'External system', detail: 'owns the data', color: 'orange', width: 320},
    {id: 'source', label: 'Source', detail: 'client', color: 'blue', step: 1, width: 320},
    {id: 'replica', label: 'Local model', detail: 'replica', color: 'purple', step: 2},
  ],
  edges: [
    {from: 'owner', to: 'source'},
    {from: 'source', to: 'replica'},
  ],
};

/** A server: the local model owns the data and the server exposes it to clients. */
export const serverRow: FlowDefinition = {
  direction: 'right',
  layerSpacing: 150,
  nodes: [
    {id: 'owner', label: 'Local model', detail: 'owns the data', color: 'purple', width: 320},
    {id: 'server', label: 'Server', detail: 'exposes it', color: 'blue', step: 1, width: 320},
    {id: 'clients', label: 'Clients', detail: 'read and write', color: 'cyan', step: 2},
  ],
  edges: [
    {from: 'owner', to: 'server'},
    {from: 'server', to: 'clients'},
    {from: 'clients', to: 'server', step: 3},
    {from: 'server', to: 'owner', step: 3},
  ],
};

/** A single source node, placed beside the mirror's tree to claim its properties. */
export function sourceNode(label: string, detail: string, color: 'blue' | 'orange'): FlowDefinition {
  return {nodes: [{id: 'source', label, detail, color, width: 320}], edges: []};
}

/** The two paths between a model and an external system. */
export const twoPaths: FlowDefinition = {
  direction: 'right',
  layerSpacing: 420,
  nodes: [
    {id: 'external', label: 'External system', color: 'orange', width: 340, height: 200, position: {x: -460, y: 0}},
    {id: 'model', label: 'Your model', color: 'purple', width: 340, height: 200, step: 1, position: {x: 460, y: 0}},
  ],
  edges: [
    {from: 'external', to: 'model', label: 'property writer', step: 1},
    {from: 'model', to: 'external', label: 'change queue', step: 2},
  ],
};

const left = -600;
const right = 600;
const top = -250;
const bottom = 40;

/** The live sample: from the simulator in the server process to the mirror and its page in the client process. */
export const livePath: FlowDefinition = {
  direction: 'right',
  nodes: [
    {id: 'simulator', label: 'Simulator', detail: 'server process', color: 'orange', width: 280, position: {x: left, y: top}},
    {id: 'machine', label: 'Machine', detail: 'server process', color: 'purple', width: 280, position: {x: -200, y: top}},
    {id: 'handler', label: 'WebSocket handler', detail: 'server, /ws', color: 'blue', width: 320, position: {x: 200, y: top}},
    {id: 'serverPage', label: 'Status page', detail: 'server :5310', color: 'green', width: 280, position: {x: -200, y: bottom}, step: 1},
    {id: 'source', label: 'Client source', detail: 'client process', color: 'cyan', width: 280, position: {x: right, y: top}, step: 2},
    {id: 'mirror', label: 'Mirror', detail: 'client process', color: 'purple', width: 280, position: {x: right, y: bottom}, step: 3},
    {id: 'clientPage', label: 'Status page', detail: 'client :5311', color: 'green', width: 280, position: {x: 200, y: bottom}, step: 3},
  ],
  edges: [
    {from: 'simulator', to: 'machine'},
    {from: 'machine', to: 'handler'},
    {from: 'machine', to: 'serverPage'},
    {from: 'handler', to: 'source'},
    {from: 'source', to: 'mirror'},
    {from: 'mirror', to: 'clientPage'},
    {from: 'mirror', to: 'source', step: 4},
    {from: 'source', to: 'handler', step: 4},
    {from: 'handler', to: 'machine', step: 4},
  ],
};

/** What a source does every time it connects. */
export const connectSteps: FlowDefinition = {
  direction: 'right',
  layerSpacing: 110,
  nodes: [
    {id: 'buffer', label: 'Buffer', detail: 'hold updates', color: 'cyan', width: 290},
    {id: 'load', label: 'Load', detail: 'initial state', color: 'purple', width: 290, step: 1},
    {id: 'replay', label: 'Replay', detail: 'buffered, in order', color: 'blue', width: 290, step: 2},
    {id: 'reconcile', label: 'Reconcile', detail: 'queued writes', color: 'green', width: 290, step: 3},
  ],
  edges: [
    {from: 'buffer', to: 'load'},
    {from: 'load', to: 'replay'},
    {from: 'replay', to: 'reconcile'},
  ],
};

/** Outbound writes of a source and where a failed one waits. */
export const retryPath: FlowDefinition = {
  direction: 'right',
  layerSpacing: 220,
  nodes: [
    {id: 'queue', label: 'Change queue', detail: 'local writes', color: 'purple'},
    {id: 'source', label: 'Source', detail: 'WriteChangesAsync', color: 'blue', width: 320},
    {id: 'external', label: 'External system', detail: 'offline', color: 'orange', width: 320},
  ],
  edges: [
    {from: 'queue', to: 'source'},
    {from: 'source', to: 'external'},
  ],
};

/** The grinder device owns the grind size, so its connector is a source. */
export const grinder: FlowDefinition = {
  direction: 'right',
  layerSpacing: 170,
  nodes: [
    {id: 'device', label: 'Grinder device', detail: 'owns GrindSize', color: 'orange', width: 320},
    {id: 'source', label: 'Grinder source', detail: 'SubjectSourceBase', color: 'blue', width: 320, step: 1},
    {id: 'hopper', label: 'Bean hopper', detail: 'GrindSize', color: 'green', step: 2},
  ],
  edges: [
    {from: 'device', to: 'source'},
    {from: 'source', to: 'hopper'},
    {from: 'hopper', to: 'source', step: 3},
    {from: 'source', to: 'device', step: 3},
  ],
};

/** The server of the sample with both of its connectors. */
export const twoConnectors: FlowDefinition = {
  direction: 'right',
  layerSpacing: 90,
  nodes: [
    {id: 'device', label: 'Grinder device', detail: 'GrindSize', color: 'orange', width: 310},
    {id: 'source', label: 'Grinder source', detail: 'source', color: 'blue', width: 270},
    {id: 'machine', label: 'Machine', detail: 'server process', color: 'purple', width: 270},
    {id: 'handler', label: 'WebSocket handler', detail: 'server', color: 'blue', width: 290},
    {id: 'mirror', label: 'Mirror', detail: 'client process', color: 'cyan', width: 270},
  ],
  edges: [
    {from: 'device', to: 'source'},
    {from: 'source', to: 'machine'},
    {from: 'machine', to: 'handler'},
    {from: 'handler', to: 'mirror'},
  ],
};
