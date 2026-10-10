import {makeProject, type SceneDescription} from '@revideo/core';
import './fonts';
import {palette} from './palette';

export function episodeProject(scenes: SceneDescription<any>[]) {
  const draft = typeof __RENDER_PRESET__ !== 'undefined' && __RENDER_PRESET__ === 'draft';
  return makeProject({
    scenes,
    // Code draw hooks, used to draw code without ligatures, are an experimental Revideo feature.
    experimentalFeatures: true,
    settings: {
      shared: {size: {x: 1920, y: 1080}, background: palette.background},
      rendering: {fps: draft ? 15 : 30},
    },
  });
}
