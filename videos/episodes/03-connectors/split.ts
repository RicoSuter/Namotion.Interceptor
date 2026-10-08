import type {Frame, Page} from 'playwright';

/** Width of one status page; the split page shows the server left and the client right. */
export const columnWidth = 800;

export interface MachineStatus {
  status: string;
  isReady: boolean;
  temperature: number;
  pressure: number;
  cupsBrewed: number;
}

/** Shows both status pages side by side and returns their frames once both show live values. */
export async function openSplit(page: Page, baseUrls: Record<string, string>): Promise<{server: Frame; client: Frame}> {
  await page.setContent(`<!DOCTYPE html>
    <html style="background:#1c1c1e"><body style="margin:0;display:flex;overflow:hidden">
      <iframe name="server" src="${baseUrls.server}/" style="width:${columnWidth}px;height:100vh;border:0"></iframe>
      <iframe name="client" src="${baseUrls.client}/" style="width:${columnWidth}px;height:100vh;border:0"></iframe>
    </body></html>`);
  const server = await frame(page, 'server');
  const client = await frame(page, 'client');
  for (const target of [server, client]) {
    await target.waitForFunction(() => document.getElementById('status')?.textContent !== 'Connecting');
  }
  return {server, client};
}

export async function readStatus(baseUrl: string): Promise<MachineStatus> {
  const response = await fetch(`${baseUrl}/status`);
  return (await response.json()) as MachineStatus;
}

export async function waitForStatus(baseUrl: string, condition: (status: MachineStatus) => boolean, timeoutSeconds = 90): Promise<void> {
  const deadline = Date.now() + timeoutSeconds * 1000;
  while (Date.now() < deadline) {
    if (condition(await readStatus(baseUrl))) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  throw new Error(`${baseUrl} did not reach the expected status within ${timeoutSeconds} s`);
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
