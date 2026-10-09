import type {Demo, DemoPreparation} from '../../../tools/capture/config';
import {openSplit, readStatus, readyAndQuiet, reset} from '../split';

/** A ready machine with an empty change stream, so the brew's changes are the first rows. */
export const prepare: DemoPreparation = async ({baseUrl}) => {
  await reset(baseUrl, 'brew');
  await readyAndQuiet(baseUrl, 'brew');
};

const demo: Demo = async (page, {baseUrl, mark}) => {
  const cups = (await readStatus(baseUrl, 'brew')).cupsBrewed;
  const {left, right} = await openSplit(page, `${baseUrl}/brew/`, `${baseUrl}/brew/changes`);
  mark('open');
  await page.waitForTimeout(3000);
  mark('click');
  await left.click('#brew-espresso');
  await right.waitForFunction(() => document.querySelectorAll('.entry').length >= 6, null, {timeout: 10_000});
  mark('rows');
  await left.waitForFunction(() => parseFloat(document.getElementById('pressure')?.textContent ?? '0') >= 9, null, {timeout: 10_000});
  mark('pressure');
  await left.waitForFunction(expected => document.getElementById('cups')?.textContent === String(expected), cups + 1, {timeout: 60_000});
  mark('done');
  // Hold on the end state so the clip ends on the finishing rows.
  await page.waitForTimeout(3000);
};

export default demo;
