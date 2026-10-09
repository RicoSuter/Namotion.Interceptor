import {defineCapture} from '../../tools/capture/config';

export default defineCapture({
  app: {project: 'sample/Tracking.Sample.csproj', port: 5320, readyPath: '/brew/status'},
  // Two 800 by 760 pages side by side; the scenes crop each half into its own window.
  viewport: {width: 1600, height: 760},
  terminal: [
    {
      name: 'run-sample',
      command: 'dotnet',
      args: ['run', '--project', 'sample', '--urls', 'http://localhost:5321'],
      until: 'is ready: True',
      timeoutSeconds: 180,
    },
  ],
});
