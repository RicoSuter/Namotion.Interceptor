import {closeSync, fstatSync, openSync, readSync, writeSync} from 'node:fs';

interface Box {
  type: string;
  start: number;
  /** Offset of the payload, after the size and type fields. */
  payload: number;
  end: number;
}

const subtitleHandlers = new Set(['sbtl', 'text', 'subt']);
const trackEnabledFlag = 0x01;

/**
 * Clears the enabled flag of every subtitle track in an MP4 file, in place, so players leave the subtitles off
 * until the viewer turns them on. Returns the number of tracks changed.
 */
export function disableSubtitleTracks(file: string): number {
  const descriptor = openSync(file, 'r+');
  try {
    const size = fstatSync(descriptor).size;
    const header = Buffer.alloc(16);
    let offset = 0;
    while (offset + 8 <= size) {
      readSync(descriptor, header, 0, 16, offset);
      const box = readBoxHeader(header, 0, size - offset);
      if (box.type === 'moov') {
        const moov = Buffer.alloc(box.end);
        readSync(descriptor, moov, 0, box.end, offset);
        const changed = disableSubtitleTracksInMovie(moov);
        if (changed > 0) {
          writeSync(descriptor, moov, 0, moov.length, offset);
        }
        return changed;
      }
      offset += box.end;
    }
    throw new Error(`${file} has no moov box`);
  } finally {
    closeSync(descriptor);
  }
}

/** Patches the tracks of a moov box held in memory; exported for tests. */
export function disableSubtitleTracksInMovie(moov: Buffer): number {
  const movie = readBoxHeader(moov, 0, moov.length);
  let changed = 0;
  for (const track of children(moov, movie).filter(box => box.type === 'trak')) {
    const trackBoxes = children(moov, track);
    const header = trackBoxes.find(box => box.type === 'tkhd');
    const media = trackBoxes.find(box => box.type === 'mdia');
    const handler = media ? children(moov, media).find(box => box.type === 'hdlr') : undefined;
    // hdlr payload: version and flags (4 bytes), pre_defined (4 bytes), handler type (4 bytes).
    const handlerType = handler ? moov.toString('latin1', handler.payload + 8, handler.payload + 12) : '';
    if (header && subtitleHandlers.has(handlerType)) {
      // tkhd payload starts with a version byte and three flag bytes; the enabled flag is the lowest bit.
      moov[header.payload + 3] &= ~trackEnabledFlag;
      changed++;
    }
  }
  return changed;
}

function readBoxHeader(buffer: Buffer, start: number, available: number): Box {
  const declared = buffer.readUInt32BE(start);
  const type = buffer.toString('latin1', start + 4, start + 8);
  if (declared === 1) {
    return {type, start, payload: start + 16, end: start + Number(buffer.readBigUInt64BE(start + 8))};
  }
  return {type, start, payload: start + 8, end: start + (declared === 0 ? available : declared)};
}

function children(buffer: Buffer, parent: Box): Box[] {
  const boxes: Box[] = [];
  let offset = parent.payload;
  while (offset + 8 <= parent.end) {
    const box = readBoxHeader(buffer, offset, parent.end - offset);
    if (box.end <= offset || box.end > parent.end) {
      throw new Error(`Malformed MP4 box '${box.type}' at ${offset}`);
    }
    boxes.push(box);
    offset = box.end;
  }
  return boxes;
}
