import {mkdtempSync, readFileSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {describe, expect, it} from 'vitest';
import {disableSubtitleTracks, disableSubtitleTracksInMovie} from './mp4';

function box(type: string, ...payload: Buffer[]): Buffer {
  const body = Buffer.concat(payload);
  const header = Buffer.alloc(8);
  header.writeUInt32BE(body.length + 8, 0);
  header.write(type, 4, 'latin1');
  return Buffer.concat([header, body]);
}

/** A track with an enabled tkhd (flags 0x000003) and the given handler type. */
function track(handler: string): Buffer {
  const trackHeader = box('tkhd', Buffer.from([0, 0, 0, 3]), Buffer.alloc(80));
  const handlerBox = box('hdlr', Buffer.alloc(8), Buffer.from(handler, 'latin1'), Buffer.alloc(13));
  return box('trak', trackHeader, box('mdia', box('mdhd', Buffer.alloc(24)), handlerBox));
}

/** The flags byte of the tkhd of the track at the given index. */
function trackFlags(moov: Buffer, index: number): number {
  let found = -1;
  for (let offset = moov.indexOf('tkhd', 0, 'latin1'); offset >= 0; offset = moov.indexOf('tkhd', offset + 4, 'latin1')) {
    if (++found === index) {
      return moov[offset + 4 + 3];
    }
  }
  throw new Error(`No track ${index}`);
}

describe('disableSubtitleTracksInMovie', () => {
  it('WhenMovieHasSubtitleTrack_ThenClearsOnlyItsEnabledFlag', () => {
    // Arrange
    const moov = box('moov', box('mvhd', Buffer.alloc(100)), track('vide'), track('soun'), track('sbtl'));

    // Act
    const changed = disableSubtitleTracksInMovie(moov);

    // Assert
    expect(changed).toBe(1);
    expect([trackFlags(moov, 0), trackFlags(moov, 1), trackFlags(moov, 2)]).toEqual([3, 3, 2]);
  });

  it('WhenMovieHasNoSubtitleTrack_ThenChangesNothing', () => {
    // Arrange
    const moov = box('moov', track('vide'), track('soun'));
    const before = Buffer.from(moov);

    // Act
    const changed = disableSubtitleTracksInMovie(moov);

    // Assert
    expect(changed).toBe(0);
    expect(moov.equals(before)).toBe(true);
  });
});

describe('disableSubtitleTracks', () => {
  it('WhenMoovFollowsMediaData_ThenPatchesTheFileInPlace', () => {
    // Arrange
    const file = join(mkdtempSync(join(tmpdir(), 'mp4-')), 'video.mp4');
    const media = Buffer.concat([box('ftyp', Buffer.from('isom', 'latin1')), box('mdat', Buffer.alloc(64))]);
    writeFileSync(file, Buffer.concat([media, box('moov', track('vide'), track('text'))]));

    // Act
    const changed = disableSubtitleTracks(file);

    // Assert
    const moov = readFileSync(file).subarray(media.length);
    expect(changed).toBe(1);
    expect([trackFlags(moov, 0), trackFlags(moov, 1)]).toEqual([3, 2]);
  });
});
