import type {Demo, DemoPreparation} from '../../../tools/capture/config';
import {openSplit, post, readyAndQuiet, reset} from '../split';

/** Both machines ready, both with the Ristretto recipe that asks for 97 °C. */
export const prepare: DemoPreparation = async ({baseUrl}) => {
  for (const machine of ['brew', 'brew-async']) {
    await reset(baseUrl, machine);
    await post(baseUrl, `/${machine}/recipes`);
  }
  for (const machine of ['brew', 'brew-async']) {
    await readyAndQuiet(baseUrl, machine);
  }
};

const demo: Demo = async (page, {baseUrl, mark}) => {
  const {left, right} = await openSplit(page, `${baseUrl}/brew/`, `${baseUrl}/brew-async/`);
  mark('open');
  await page.waitForTimeout(3000);

  mark('left');
  await left.click('#brew-ristretto');
  await left.waitForFunction(() => document.getElementById('status')?.textContent === 'Brewing Ristretto', null, {timeout: 10_000});
  await page.waitForTimeout(4500);

  mark('right');
  await right.click('#brew-ristretto');
  await right.waitForFunction(() => document.getElementById('error')?.classList.contains('shown'), null, {timeout: 10_000});
  await page.waitForTimeout(4500);

  mark('espresso');
  await left.click('#brew-espresso');
  await page.waitForTimeout(600);
  await right.click('#brew-espresso');
  await right.waitForFunction(() => document.getElementById('status')?.textContent === 'Brewing Espresso', null, {timeout: 10_000});
  mark('brewing');
  await right.waitForFunction(() => parseFloat(document.getElementById('pressure')?.textContent ?? '0') >= 9, null, {timeout: 10_000});
  mark('pressure');
  // Hold on the end state: the left machine refuses, the right one brews.
  await page.waitForTimeout(3500);
};

export default demo;
