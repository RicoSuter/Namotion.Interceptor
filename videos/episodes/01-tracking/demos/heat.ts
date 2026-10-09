import type {Demo, DemoPreparation} from '../../../tools/capture/config';
import {openSplit, reset} from '../split';

/** Starts the machine cold with an empty change stream. */
export const prepare: DemoPreparation = async ({baseUrl}) => {
  await reset(baseUrl, 'brew');
};

const demo: Demo = async (page, {baseUrl, mark}) => {
  const {left} = await openSplit(page, `${baseUrl}/brew/`, `${baseUrl}/brew/changes`);
  mark('open');
  await left.waitForFunction(() => parseFloat(document.getElementById('temperature')?.textContent ?? '0') >= 60, null, {timeout: 60_000});
  mark('warm');
  await left.waitForFunction(() => document.getElementById('ready-value')?.textContent === 'true', null, {timeout: 60_000});
  mark('ready');
  // Hold on the end state so the clip ends on a ready machine.
  await page.waitForTimeout(3000);
};

export default demo;
