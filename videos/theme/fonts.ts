import '@fontsource/inter/400.css';
import '@fontsource/inter/500.css';
import '@fontsource/inter/600.css';
import '@fontsource/inter/700.css';
import '@fontsource/jetbrains-mono/400.css';
import '@fontsource/jetbrains-mono/600.css';
import type {ThreadGenerator} from '@revideo/core';
import {fonts} from './style';

const faces = [
  `400 32px ${fonts.text}`,
  `500 32px ${fonts.text}`,
  `600 32px ${fonts.text}`,
  `700 32px ${fonts.text}`,
  `400 32px "${fonts.code}"`,
  `600 32px "${fonts.code}"`,
];

let loading: Promise<unknown> | undefined;

/**
 * Loads the theme fonts. Yield it before the first frame: canvas text does not trigger font loading,
 * so code drawn before the fonts arrive would use a fallback face.
 */
export function* waitForFonts(): ThreadGenerator {
  loading ??= Promise.all(faces.map(face => document.fonts.load(face)));
  yield loading;
}
