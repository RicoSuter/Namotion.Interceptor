import type {Frame, Page} from 'playwright';

/**
 * Shows two pages side by side, each in one half of the viewport, and returns their frames once both pages set
 * `document.body.dataset.live` to `'true'`. A scene shows the halves of the recording in two windows with
 * `SplitWindows`.
 */
export async function openSplit(page: Page, leftUrl: string, rightUrl: string): Promise<{left: Frame; right: Frame}> {
  await page.setContent(`<!DOCTYPE html>
    <html style="background:#1c1c1e"><body style="margin:0;display:flex;overflow:hidden">
      <iframe name="left" src="${leftUrl}" style="width:50vw;height:100vh;border:0"></iframe>
      <iframe name="right" src="${rightUrl}" style="width:50vw;height:100vh;border:0"></iframe>
    </body></html>`);
  const left = await frame(page, 'left');
  const right = await frame(page, 'right');
  for (const target of [left, right]) {
    await target.waitForFunction(() => document.body.dataset.live === 'true');
  }
  return {left, right};
}

async function frame(page: Page, name: string): Promise<Frame> {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    const found = page.frame({name});
    if (found) {
      await found.waitForLoadState('load');
      return found;
    }
    await page.waitForTimeout(50);
  }
  throw new Error(`Frame '${name}' did not load`);
}
