import {describe, expect, it} from 'vitest';
import {gatedLoudness, loudnessReport, loudnessWindows, parseLoudnessFrames} from './loudness';

const log = [
  '[Parsed_ebur128_0 @ 0x1] t: 0.1       TARGET:-23 LUFS    M:-120.7 S:-120.7     I: -70.0 LUFS       LRA:   0.0 LU  FTPK:  -inf  -inf dBFS  TPK:  -inf  -inf dBFS',
  '[Parsed_ebur128_0 @ 0x1] t: 41.4       TARGET:-23 LUFS    M: -16.2 S:  -6.2     I:  -9.9 LUFS       LRA:  12.6 LU  FTPK:  -3.5  -2.1 dBFS  TPK:   1.0   2.0 dBFS',
  'size=N/A time=00:00:41.98 bitrate=N/A speed= 168x',
].join('\n');

describe('parseLoudnessFrames', () => {
  it('WhenLogHasFrameLines_ThenReturnsTimeMomentaryAndLoudestChannelPeak', () => {
    // Act
    const frames = parseLoudnessFrames(log);

    // Assert
    expect(frames).toEqual([
      {time: 0.1, momentary: -120.7, truePeak: -Infinity},
      {time: 41.4, momentary: -16.2, truePeak: -2.1},
    ]);
  });
});

describe('gatedLoudness', () => {
  it('WhenBlocksAreEquallyLoud_ThenReturnsTheirLoudness', () => {
    // Act & Assert
    expect(gatedLoudness([-16, -16, -16])).toBeCloseTo(-16);
  });

  it('WhenBlocksAreSilentOrFarBelowTheRest_ThenTheGatesDropThem', () => {
    // Act & Assert
    expect(gatedLoudness([-16, -16, -80, -40])).toBeCloseTo(-16);
    expect(gatedLoudness([-90, -120])).toBeNull();
  });
});

describe('loudnessWindows', () => {
  it('WhenFramesSpanSeveralWindows_ThenEachWindowGetsItsOwnLoudnessAndPeak', () => {
    // Arrange
    const frames = [
      {time: 10, momentary: -16, truePeak: -3},
      {time: 30, momentary: -16, truePeak: -2},
      {time: 40, momentary: -20, truePeak: -5},
    ];

    // Act
    const windows = loudnessWindows(frames, 70, 30);

    // Assert
    expect(windows.map(window => [window.start, window.end, window.integrated, window.truePeak])).toEqual([
      [0, 30, expect.closeTo(-16), -2],
      [30, 60, expect.closeTo(-20), -5],
      [60, 70, null, -Infinity],
    ]);
  });
});

describe('loudnessReport', () => {
  it('WhenAWindowIsFarFromTheIntegratedLoudness_ThenItIsListed', () => {
    // Act
    const report = loudnessReport({
      integrated: -16,
      truePeak: -1.9,
      windows: [{start: 0, end: 30, integrated: -16.2, truePeak: -2}, {start: 30, end: 60, integrated: -19, truePeak: -4}],
    }).join('\n');

    // Assert
    expect(report).toContain('Integrated -16.0 LUFS (target -16 LUFS), true peak -1.9 dBTP.');
    expect(report).toContain('Windows more than 2 LU from the integrated loudness: 0:30 (-19.0 LUFS).');
    expect(report).toContain('| 0:00 to 0:30 | -16.2 LUFS | -2.0 dBTP |');
  });

  it('WhenEveryWindowIsClose_ThenTheTrackIsFlat', () => {
    // Act
    const report = loudnessReport({integrated: -16, truePeak: -2, windows: [{start: 0, end: 30, integrated: -15, truePeak: -2}]}).join('\n');

    // Assert
    expect(report).toContain('Flat: every 30 s window is within 2 LU of the integrated loudness.');
  });
});
