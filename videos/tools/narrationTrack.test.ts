import {describe, expect, it} from 'vitest';
import {narrationTrackArgs} from './narrationTrack';

const timing = {
  episode: 'smoke',
  title: 'Smoke',
  totalDuration: 6,
  beats: [
    {id: 'a', chapter: 'one', chapterTitle: 'One', start: 0, duration: 2.5, audio: '/generated/smoke/audio/aa.wav', caption: 'Hello.'},
    {id: 'b', chapter: 'one', chapterTitle: 'One', start: 2.5, duration: 1, audio: null, caption: null},
    {id: 'c', chapter: 'one', chapterTitle: 'One', start: 3.5, duration: 2.5, audio: '/generated/smoke/audio/cc.wav', caption: 'Bye.'},
  ],
};

describe('narrationTrackArgs', () => {
  it('WhenAllBeatsAreIncluded_ThenEachBeatIsPaddedToItsDurationAndJoined', () => {
    // Act
    const args = narrationTrackArgs(timing, null, '/videos/public', 'out.wav');

    // Assert
    const format = 'aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo';
    expect(args).toEqual([
      '-i', '/videos/public/generated/smoke/audio/aa.wav',
      '-f', 'lavfi', '-t', '1.000000', '-i', 'anullsrc=r=48000:cl=stereo',
      '-i', '/videos/public/generated/smoke/audio/cc.wav',
      '-filter_complex', [
        `[0:a]${format},apad,atrim=end=2.500000,asetpts=PTS-STARTPTS[beat0]`,
        `[1:a]${format}[beat1]`,
        `[2:a]${format},apad,atrim=end=2.500000,asetpts=PTS-STARTPTS[beat2]`,
        '[beat0][beat1][beat2]concat=n=3:v=0:a=1[narration]',
      ].join(';'),
      '-map', '[narration]', '-c:a', 'pcm_s16le', 'out.wav',
    ]);
  });

  it('WhenBeatsAreSelected_ThenOnlyTheyAreJoinedInScriptOrder', () => {
    // Act
    const args = narrationTrackArgs(timing, new Set(['c', 'a']), '/videos/public', 'out.wav');

    // Assert
    expect(args.filter(argument => argument.endsWith('.wav'))).toEqual(['/videos/public/generated/smoke/audio/aa.wav', '/videos/public/generated/smoke/audio/cc.wav', 'out.wav']);
    expect(args).toContain('-filter_complex');
    expect(args[args.indexOf('-filter_complex') + 1]).toContain('concat=n=2');
  });

  it('WhenNoBeatIsSelected_ThenThrows', () => {
    // Act & Assert
    expect(() => narrationTrackArgs(timing, new Set(['x']), '/videos/public', 'out.wav')).toThrow(/No beats/);
  });
});
