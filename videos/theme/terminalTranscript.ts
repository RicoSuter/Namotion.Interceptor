export interface TerminalTranscript {
  command: string;
  output: string[];
}

/** Splits a captured transcript into its prompt command and output lines, dropping trailing blank lines. */
export function parseTranscript(transcript: string): TerminalTranscript {
  const [first, ...rest] = transcript.replace(/\r\n/g, '\n').split('\n');
  const command = first.startsWith('$ ') ? first.slice(2) : first;
  while (rest.length > 0 && rest[rest.length - 1].trim() === '') {
    rest.pop();
  }
  return {command, output: rest};
}

/**
 * Wraps lines longer than the column count, preferring the last space that keeps at least half a row,
 * so words and addresses stay whole where possible.
 */
export function wrapLines(lines: string[], columns: number): string[] {
  return lines.flatMap(line => {
    const rows: string[] = [];
    let rest = line;
    while (rest.length > columns) {
      const space = rest.lastIndexOf(' ', columns);
      const breakAt = space >= columns / 2 ? space : columns;
      rows.push(rest.slice(0, breakAt).trimEnd());
      rest = rest.slice(breakAt).trimStart();
    }
    rows.push(rest);
    return rows;
  });
}
