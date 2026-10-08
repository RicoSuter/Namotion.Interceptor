import {makeProject, type SceneDescription} from '@revideo/core';
import {palette} from './palette';

export function episodeProject(scenes: SceneDescription<any>[]) {
  const draft = typeof __RENDER_PRESET__ !== 'undefined' && __RENDER_PRESET__ === 'draft';
  return makeProject({
    scenes,
    settings: {
      shared: {size: {x: 1920, y: 1080}, background: palette.base},
      rendering: {fps: draft ? 15 : 30},
    },
  });
}
