import {defineCapture} from '../../tools/capture/config';

export default defineCapture({
  apps: [
    {name: 'server', project: 'sample/Server/Connectors.Server.csproj', port: 5310, readyPath: '/status'},
    {name: 'client', project: 'sample/Client/Connectors.Client.csproj', port: 5311, readyPath: '/status'},
  ],
  // Two 800 by 720 status pages side by side; the scenes crop each half into its own window.
  viewport: {width: 1600, height: 720},
  terminal: [
    {
      name: 'run-client',
      command: 'dotnet',
      args: ['run', '--project', 'sample/Client', '--urls', 'http://localhost:5312'],
      until: 'Claimed ownership of \\d+ properties',
      timeoutSeconds: 180,
      whileAppsRun: true,
    },
  ],
});
