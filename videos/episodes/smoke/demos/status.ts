import type {Demo} from '../../../tools/capture/config';

const demo: Demo = async (page, {baseUrl}) => {
  await page.goto(baseUrl);
  await page.waitForFunction(() => document.getElementById('status')?.textContent === 'Ready', null, {timeout: 60_000});
  // Hold on the end state so the clip ends on "Ready".
  await page.waitForTimeout(1500);
};

export default demo;
