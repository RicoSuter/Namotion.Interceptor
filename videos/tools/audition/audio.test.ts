import {describe, expect, it} from 'vitest';
import {concatWithGapsArgs, countWords, normalizeLoudnessArgs, parseLoudnessMeasurement, wordsPerMinute} from './audio';

const loudnormLog = `size=N/A time=00:00:07.20 bitrate=N/A speed= 361x
[Parsed_loudnorm_0 @ 0x3fdb1680]
{
	"input_i" : "-21.30",
	"input_tp" : "-3.12",
	"input_lra" : "4.10",
	"input_thresh" : "-31.60",
	"output_i" : "-16.02",
	"output_tp" : "-1.50",
	"output_lra" : "3.90",
	"output_thresh" : "-26.30",
	"normalization_type" : "dynamic",
	"target_offset" : "-0.02"
}
`;

describe('parseLoudnessMeasurement', () => {
  it('WhenLogEndsWithMeasurement_ThenReturnsIt', () => {
    // Act
    const measurement = parseLoudnessMeasurement(loudnormLog);

    // Assert
    expect(measurement.input_i).toBe('-21.30');
    expect(measurement.input_tp).toBe('-3.12');
    expect(measurement.target_offset).toBe('-0.02');
  });

  it('WhenLogHasNoMeasurement_ThenThrows', () => {
    // Act & Assert
    expect(() => parseLoudnessMeasurement('Invalid data found')).toThrow(/No loudnorm measurement/);
  });
});

describe('normalizeLoudnessArgs', () => {
  it('WhenMeasured_ThenSecondPassAppliesALinearGainFromTheMeasurement', () => {
    // Act
    const args = normalizeLoudnessArgs('in.wav', parseLoudnessMeasurement(loudnormLog), 'out.wav');

    // Assert
    expect(args).toEqual([
      '-i', 'in.wav',
      '-af', 'loudnorm=I=-16:TP=-1.5:LRA=11:measured_I=-21.30:measured_TP=-3.12:measured_LRA=4.10:measured_thresh=-31.60:offset=-0.02:linear=true,aresample=24000',
      '-ar', '24000', '-ac', '1', '-c:a', 'pcm_s16le', 'out.wav',
    ]);
  });
});

describe('concatWithGapsArgs', () => {
  it('WhenFilesAreJoined_ThenSilenceSitsBetweenThemOnly', () => {
    // Act
    const args = concatWithGapsArgs(['a.wav', 'b.wav', 'c.wav'], 0.6, 'all.wav');

    // Assert
    const silence = ['-f', 'lavfi', '-t', '0.6', '-i', 'anullsrc=r=24000:cl=mono'];
    expect(args).toEqual([
      '-i', 'a.wav', ...silence, '-i', 'b.wav', ...silence, '-i', 'c.wav',
      '-filter_complex', '[0:a][1:a][2:a][3:a][4:a]concat=n=5:v=0:a=1[out]', '-map', '[out]',
      '-ar', '24000', '-ac', '1', '-c:a', 'pcm_s16le', 'all.wav',
    ]);
  });

  it('WhenThereIsOneFile_ThenNoSilenceIsAdded', () => {
    // Act
    const args = concatWithGapsArgs(['a.wav'], 1.5, 'all.wav');

    // Assert
    expect(args.slice(0, 4)).toEqual(['-i', 'a.wav', '-filter_complex', '[0:a]concat=n=1:v=0:a=1[out]']);
  });
});

describe('wordsPerMinute', () => {
  it('WhenWordsAreCounted_ThenPunctuationAndSpacingDoNotMatter', () => {
    // Act & Assert
    expect(countWords('  Nothing is lost,  while the network is down. ')).toBe(8);
    expect(wordsPerMinute(30, 10)).toBe(180);
  });
});
