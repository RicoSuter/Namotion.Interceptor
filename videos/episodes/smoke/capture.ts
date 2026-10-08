import {defineCapture} from '../../tools/capture/config';

export default defineCapture({
  app: {project: 'sample/Smoke.Sample.csproj', port: 5280, readyPath: '/status'},
  viewport: {width: 1280, height: 800},
  terminal: [
    {
      name: 'run-sample',
      command: 'dotnet',
      args: ['run', '--project', 'sample', '--urls', 'http://localhost:5281'],
      until: 'Now listening on',
      timeoutSeconds: 180,
    },
  ],
});
