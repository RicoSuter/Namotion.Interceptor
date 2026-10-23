import {defineConfig} from 'vitest/config';

export default defineConfig({
  test: {
    include: ['theme/**/*.test.ts', 'tools/**/*.test.ts'],
  },
});
