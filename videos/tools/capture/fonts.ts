import {readFileSync} from 'node:fs';
import {join} from 'node:path';
import {videosRoot} from '../paths';

const faces = [
  {family: 'Inter', weight: '400', file: '@fontsource/inter/files/inter-latin-400-normal.woff2'},
  {family: 'Inter', weight: '500', file: '@fontsource/inter/files/inter-latin-500-normal.woff2'},
  {family: 'Inter', weight: '600', file: '@fontsource/inter/files/inter-latin-600-normal.woff2'},
  {family: 'Inter', weight: '700', file: '@fontsource/inter/files/inter-latin-700-normal.woff2'},
  {family: 'JetBrains Mono', weight: '400', file: '@fontsource/jetbrains-mono/files/jetbrains-mono-latin-400-normal.woff2'},
  {family: 'JetBrains Mono', weight: '600', file: '@fontsource/jetbrains-mono/files/jetbrains-mono-latin-600-normal.woff2'},
];

/**
 * Browser init script that registers the theme fonts in every demo page, so sample pages can name
 * Inter and JetBrains Mono without shipping the font files themselves.
 */
export function themeFontsScript(): string {
  const data = faces.map(face => ({
    family: face.family,
    weight: face.weight,
    data: readFileSync(join(videosRoot, 'node_modules', face.file)).toString('base64'),
  }));
  return `(() => {
    for (const face of ${JSON.stringify(data)}) {
      const bytes = Uint8Array.from(atob(face.data), character => character.charCodeAt(0));
      const font = new FontFace(face.family, bytes, {weight: face.weight});
      document.fonts.add(font);
      font.load();
    }
  })();`;
}
