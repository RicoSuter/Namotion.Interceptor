import type {Demo} from '../../../tools/capture/config';

const demo: Demo = async (page, {baseUrl}) => {
  await page.goto(`${baseUrl}/status`);
  // Wait until the simulated boiler is hot so the clip ends on "Ready".
  for (let attempt = 0; attempt < 40; attempt++) {
    await page.waitForTimeout(1000);
    await page.reload();
    if ((await page.textContent('body'))?.includes('"isReady":true')) {
      break;
    }
  }
  await page.waitForTimeout(1500);
};

export default demo;
