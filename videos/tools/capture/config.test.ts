import {describe, expect, it} from 'vitest';
import {appUrls, resolveApps} from './config';

const server = {project: 'Server/Server.csproj', port: 5310, readyPath: '/status'};
const client = {project: 'Client/Client.csproj', port: 5311, readyPath: '/status'};

describe('resolveApps', () => {
  it('WhenOnlyAppIsSet_ThenItIsNamedApp', () => {
    // Act
    const apps = resolveApps({app: server});

    // Assert
    expect(apps).toEqual([{...server, name: 'app'}]);
  });

  it('WhenAppsAreSet_ThenTheyKeepTheirOrderAndNames', () => {
    // Act
    const apps = resolveApps({apps: [{...server, name: 'server'}, {...client, name: 'client'}]});

    // Assert
    expect(apps.map(app => app.name)).toEqual(['server', 'client']);
  });

  it('WhenNothingIsSet_ThenThereAreNoApps', () => {
    // Act
    const apps = resolveApps({});

    // Assert
    expect(apps).toEqual([]);
  });

  it('WhenBothAppAndAppsAreSet_ThenItThrows', () => {
    // Act & Assert
    expect(() => resolveApps({app: server, apps: [{...client, name: 'client'}]})).toThrow(/both app and apps/);
  });

  it('WhenAnAppHasNoName_ThenItThrows', () => {
    // Act & Assert
    expect(() => resolveApps({apps: [server]})).toThrow(/needs a name/);
  });

  it('WhenNamesRepeat_ThenItThrows', () => {
    // Act & Assert
    expect(() => resolveApps({apps: [{...server, name: 'x'}, {...client, name: 'x'}]})).toThrow(/name 'x' is used twice/);
  });

  it('WhenPortsRepeat_ThenItThrows', () => {
    // Act & Assert
    expect(() => resolveApps({apps: [{...server, name: 'server'}, {...server, name: 'client'}]})).toThrow(/port 5310 is used twice/);
  });
});

describe('appUrls', () => {
  it('WhenSeveralAppsRun_ThenTheFirstIsTheBaseUrlAndAllAreNamed', () => {
    // Act
    const urls = appUrls([
      {name: 'server', baseUrl: 'http://localhost:5310'},
      {name: 'client', baseUrl: 'http://localhost:5311'},
    ]);

    // Assert
    expect(urls).toEqual({
      baseUrl: 'http://localhost:5310',
      baseUrls: {server: 'http://localhost:5310', client: 'http://localhost:5311'},
    });
  });

  it('WhenNoAppRuns_ThenItThrows', () => {
    // Act & Assert
    expect(() => appUrls([])).toThrow(/Demos need an app/);
  });
});
