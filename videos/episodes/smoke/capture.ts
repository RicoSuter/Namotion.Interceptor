import {defineCapture} from '../../tools/capture/config';

export default defineCapture({
  app: {project: 'sample/Smoke.Sample.csproj', port: 5280, readyPath: '/status'},
  viewport: {width: 1280, height: 800},
  terminal: [{name: 'dotnet-version', command: 'dotnet', args: ['--version']}],
});
