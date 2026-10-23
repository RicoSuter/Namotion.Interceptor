export function extractRegion(source: string, name: string): string {
  const lines = source.split(/\r?\n/);
  const start = lines.findIndex(line => line.trim() === `#region ${name}`);
  if (start < 0) {
    throw new Error(`Region '${name}' not found`);
  }

  const body: string[] = [];
  let depth = 0;
  for (let index = start + 1; index < lines.length; index++) {
    const trimmed = lines[index].trim();
    if (trimmed.startsWith('#region')) {
      depth++;
      continue;
    }
    if (trimmed.startsWith('#endregion')) {
      if (depth === 0) {
        return dedent(body);
      }
      depth--;
      continue;
    }
    body.push(lines[index]);
  }
  throw new Error(`Region '${name}' has no matching #endregion`);
}

function dedent(lines: string[]): string {
  let first = 0;
  let last = lines.length - 1;
  while (first <= last && lines[first].trim() === '') first++;
  while (last >= first && lines[last].trim() === '') last--;
  const content = lines.slice(first, last + 1);
  const indent = Math.min(
    ...content.filter(line => line.trim() !== '').map(line => line.length - line.trimStart().length),
  );
  return content.map(line => line.slice(Math.min(indent, line.length - line.trimStart().length))).join('\n');
}
