/** macOS dark system colors. */
export const palette = {
  background: '#1c1c1e',
  card: '#2c2c2e',
  elevated: '#3a3a3c',
  separator: '#48484a',
  text: '#f5f5f7',
  secondaryText: '#a1a1a6',
  /** Neutral connector between nodes (system gray 2): visible on the background without competing with the nodes. */
  edge: '#636366',
  blue: '#0a84ff',
  cyan: '#64d2ff',
  green: '#30d158',
  orange: '#ff9f0a',
  pink: '#ff375f',
  purple: '#bf5af2',
} as const;

export type AccentColor = 'blue' | 'cyan' | 'green' | 'orange' | 'pink' | 'purple';

/** Window control colors of a macOS title bar. */
export const trafficLights = ['#ff5f57', '#febc2e', '#28c840'] as const;

/** Syntax colors tuned for contrast on the card color. */
export const syntax = {
  keyword: '#ff7ab2',
  type: '#64d2ff',
  property: '#d0a8ff',
  method: '#67e0b8',
  string: '#ff8a7a',
  number: '#e5cf7e',
  comment: '#7f8c98',
  punctuation: '#a1a1a6',
} as const;
