import type {FlowDefinition} from '../../../theme/flowLayout';

export const statusFlow: FlowDefinition = {
  direction: 'right',
  nodes: [
    {id: 'simulator', label: 'Simulator', detail: 'heats the boiler', color: 'orange'},
    {id: 'boiler', label: 'Boiler', detail: 'Temperature, IsHot', color: 'pink', step: 1},
    {id: 'tank', label: 'Water tank', detail: 'Level, IsLow', color: 'cyan', step: 1},
    {id: 'machine', label: 'Machine', detail: 'IsReady, Status', color: 'purple', step: 2},
    {id: 'page', label: 'Status page', detail: 'polls /status', color: 'green', step: 3},
  ],
  edges: [
    {from: 'simulator', to: 'boiler'},
    {from: 'simulator', to: 'tank'},
    {from: 'boiler', to: 'machine', label: 'IsHot'},
    {from: 'tank', to: 'machine', label: 'IsLow'},
    {from: 'machine', to: 'page'},
  ],
};

export const updateParticipants = [
  {id: 'simulator', label: 'Simulator', color: 'orange'},
  {id: 'boiler', label: 'Boiler', color: 'pink'},
  {id: 'machine', label: 'Machine', color: 'purple'},
  {id: 'page', label: 'Status page', color: 'green'},
] as const;
