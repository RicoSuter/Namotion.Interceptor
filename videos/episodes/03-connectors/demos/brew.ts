import type {Demo, DemoPreparation} from '../../../tools/capture/config';
import {openSplit, readStatus, waitForStatus} from '../split';

/** Starts from a fresh machine and waits until it is ready, so the clip starts on a machine that can brew. */
export const prepare: DemoPreparation = async ({baseUrls}) => {
  await fetch(`${baseUrls.server}/reset`, {method: 'POST'});
  await waitForStatus(baseUrls.client, status => status.temperature < 25);
  await waitForStatus(baseUrls.client, status => status.isReady && status.pressure === 0);
};

const demo: Demo = async (page, {baseUrls, mark}) => {
  const cups = (await readStatus(baseUrls.server)).cupsBrewed;
  const {server, client} = await openSplit(page, baseUrls);
  mark('open');

  // Both windows at rest before the press.
  await page.waitForTimeout(4500);
  mark('click');
  await client.click('#brew');

  await server.waitForFunction(() => document.getElementById('status')?.textContent?.startsWith('Brewing'), null, {timeout: 10_000});
  mark('brewing');
  await server.waitForFunction(() => parseFloat(document.getElementById('pressure')?.textContent ?? '0') >= 9, null, {timeout: 10_000});
  mark('pressure');
  for (const target of [server, client]) {
    await target.waitForFunction(expected => document.getElementById('cups')?.textContent === String(expected), cups + 1, {timeout: 60_000});
  }
  mark('done');
  // Hold on the end state so the clip ends on the new cup count.
  await page.waitForTimeout(2500);
};

export default demo;
