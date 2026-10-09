import {describe, expect, it} from 'vitest';
import {auditionReadme, parseAuditionVoice} from './report';

describe('auditionReadme', () => {
  it('WhenVoicesAreAuditioned_ThenTableListsOrderStartTimesAndPace', () => {
    // Arrange
    const lines = [{id: 'a', purpose: 'intro', text: 'Hello there.'}];
    const results = [
      {label: 'chatterbox-default', spec: 'chatterbox', allSeconds: 20, speechSeconds: 18.8, words: 63, peakBeforeNormalization: -2.04},
      {label: 'kokoro-af_heart', spec: 'kokoro:af_heart', allSeconds: 50.5, speechSeconds: 49.3, words: 63, peakBeforeNormalization: -0.04},
      {label: 'clone-rico', spec: 'clone:voices/rico.wav', allSeconds: 10, speechSeconds: 8.8, words: 63, peakBeforeNormalization: -6},
    ];

    // Act
    const readme = auditionReadme(lines, results);

    // Assert
    expect(readme).toContain('- A (intro): Hello there.');
    expect(readme).toContain('| 1 | chatterbox-default | `chatterbox` | 0:00.0 | 20.0 s | 201 | -2.0 dBTP |');
    expect(readme).toContain('| 2 | kokoro-af_heart | `kokoro:af_heart` | 0:21.5 | 50.5 s | 77 | -0.0 dBTP |');
    expect(readme).toContain('| 3 | clone-rico | `clone:voices/rico.wav` | 1:13.5 | 10.0 s | 430 | -6.0 dBTP |');
  });
});

describe('parseAuditionVoice', () => {
  it('WhenArgumentHasALabel_ThenLabelNamesTheFiles', () => {
    // Act & Assert
    expect(parseAuditionVoice('clone-rico-raw=clone:voices/rico.wav')).toEqual({label: 'clone-rico-raw', spec: 'clone:voices/rico.wav'});
    expect(parseAuditionVoice('kokoro:bf_emma')).toEqual({label: 'kokoro-bf_emma', spec: 'kokoro:bf_emma'});
    expect(parseAuditionVoice('clone:voices/a=b.wav')).toEqual({label: 'clone-a-b', spec: 'clone:voices/a=b.wav'});
  });
});
