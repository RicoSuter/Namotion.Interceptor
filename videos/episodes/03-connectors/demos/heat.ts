import type {Demo, DemoPreparation} from '../../../tools/capture/config';
import {openSplit, waitForStatus} from '../split';

/** Cools the boiler back to room temperature on the server; the client follows. */
export const prepare: DemoPreparation = async ({baseUrls}) => {
  await fetch(`${baseUrls.server}/reset`, {method: 'POST'});
  await waitForStatus(baseUrls.client, status => status.temperature < 25);
};

const demo: Demo = async (page, {baseUrls, mark}) => {
  const {server, client} = await openSplit(page, baseUrls);
  mark('open');
  await server.waitForFunction(() => parseFloat(document.getElementById('temperature')?.textContent ?? '0') >= 60, null, {timeout: 60_000});
  mark('warm');
  for (const target of [server, client]) {
    await target.waitForFunction(() => document.getElementById('status')?.textContent === 'Ready', null, {timeout: 60_000});
  }
  mark('ready');
  // Hold on the end state so the clip ends on Ready.
  await page.waitForTimeout(2000);
};

export default demo;
