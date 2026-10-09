import type {Demo, DemoPreparation} from '../../../tools/capture/config';
import {openSplit, readyAndQuiet, reset} from '../split';

/** A ready machine with its two original recipes and an empty change stream. */
export const prepare: DemoPreparation = async ({baseUrl}) => {
  await reset(baseUrl, 'brew');
  await readyAndQuiet(baseUrl, 'brew');
};

const demo: Demo = async (page, {baseUrl, mark}) => {
  const {left, right} = await openSplit(page, `${baseUrl}/brew/`, `${baseUrl}/brew/changes`);
  mark('open');
  await page.waitForTimeout(3000);
  mark('add');
  await left.click('#add-recipe');
  await right.waitForFunction(() => document.querySelector('.entry.lifecycle') !== null, null, {timeout: 10_000});
  await left.waitForSelector('#brew-ristretto', {timeout: 10_000});
  mark('added');
  // Hold on the end state so the clip ends on the attached row and the new button.
  await page.waitForTimeout(8000);
};

export default demo;
