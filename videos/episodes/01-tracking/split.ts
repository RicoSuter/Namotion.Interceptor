import type {Frame, Page} from 'playwright';

/** Width of one page; a split page shows two of them side by side. */
export const columnWidth = 800;

export interface MachineStatus {
  status: string;
  isReady: boolean;
  temperature: number;
  pressure: number;
  cupsBrewed: number;
  recipes: string[];
}

/** Shows two pages side by side and returns their frames once both show live values. */
export async function openSplit(page: Page, leftUrl: string, rightUrl: string): Promise<{left: Frame; right: Frame}> {
  await page.setContent(`<!DOCTYPE html>
    <html style="background:#1c1c1e"><body style="margin:0;display:flex;overflow:hidden">
      <iframe name="left" src="${leftUrl}" style="width:${columnWidth}px;height:100vh;border:0"></iframe>
      <iframe name="right" src="${rightUrl}" style="width:${columnWidth}px;height:100vh;border:0"></iframe>
    </body></html>`);
  const left = await frame(page, 'left');
  const right = await frame(page, 'right');
  for (const target of [left, right]) {
    await target.waitForFunction(() => document.body.dataset.live === 'true');
  }
  return {left, right};
}

export async function readStatus(baseUrl: string, machine: string): Promise<MachineStatus> {
  const response = await fetch(`${baseUrl}/${machine}/status`);
  return (await response.json()) as MachineStatus;
}

export async function waitForStatus(baseUrl: string, machine: string, condition: (status: MachineStatus) => boolean, timeoutSeconds = 90): Promise<void> {
  const deadline = Date.now() + timeoutSeconds * 1000;
  while (Date.now() < deadline) {
    if (condition(await readStatus(baseUrl, machine))) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  throw new Error(`${machine} did not reach the expected status within ${timeoutSeconds} s`);
}

export async function post(baseUrl: string, path: string): Promise<void> {
  await fetch(`${baseUrl}${path}`, {method: 'POST'});
}

/** Starts a machine cold, with its original recipes and an empty change stream. */
export async function reset(baseUrl: string, machine: string): Promise<void> {
  await post(baseUrl, `/${machine}/reset`);
  await waitForStatus(baseUrl, machine, status => status.temperature < 25);
}

/** Waits until a machine is ready and its pump has settled, then empties its change stream. */
export async function readyAndQuiet(baseUrl: string, machine: string): Promise<void> {
  await waitForStatus(baseUrl, machine, status => status.isReady && status.pressure === 0);
  await post(baseUrl, `/${machine}/clear`);
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
