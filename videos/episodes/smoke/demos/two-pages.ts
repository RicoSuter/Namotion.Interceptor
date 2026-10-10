import type {Demo, DemoPreparation} from '../../../tools/capture/config';
import {openSplit} from '../../../tools/capture/split';

/** Waits until the machine is ready, so the clip starts with an enabled brew button instead of a heating boiler. */
export const prepare: DemoPreparation = async ({baseUrl}) => {
  const deadline = Date.now() + 90_000;
  while (Date.now() < deadline) {
    const status = (await (await fetch(`${baseUrl}/status`)).json()) as {isReady: boolean};
    if (status.isReady) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  throw new Error('The machine did not become ready within 90 s');
};

const demo: Demo = async (page, {baseUrl, mark}) => {
  const {left, right} = await openSplit(page, `${baseUrl}/controls`, `${baseUrl}/`);
  await page.waitForTimeout(2000);
  mark('click');
  await left.click('#brew');
  await right.waitForFunction(() => document.getElementById('status')?.textContent?.startsWith('Brewing'), null, {timeout: 10_000});
  mark('brewing');
  await right.waitForFunction(() => document.getElementById('status')?.textContent === 'Ready', null, {timeout: 60_000});
  mark('ready');
  // Hold on the end state so the clip ends on "Ready".
  await page.waitForTimeout(1500);
};

export default demo;
