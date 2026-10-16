import {describe, expect, it} from 'vitest';
import {parseScript} from './schema/script';
import {applyNarrationOptions, buildNarration, buildTiming, narrationPadding, tempoKey, toSrt} from './narration';

const script = parseScript(`
episode: smoke
title: Smoke
voice: chatterbox
tempo: 1
chapters:
  - id: one
    title: One
    beats:
      - id: first
        narration: Heat to 93 °C.
        visual: Boiler heats.
        components: [CodeCard]
      - id: second
        hold: 2
        visual: Pause.
        components: [CodeCard]
  - id: two
    title: Two
    beats:
      - id: third
        narration: Done.
        hold: 0.5
        visual: Outro.
        components: [ChapterCard]
`);

const lexicon = [{match: '°C', say: 'degrees'}];

describe('buildNarration', () => {
  it('WhenBeatsHaveNarration_ThenItemsUseSpokenTextAndStableKeys', () => {
    // Act
    const items = buildNarration(script, lexicon);
    const again = buildNarration(script, lexicon);

    // Assert
    expect(items.map(item => [item.beatId, item.text])).toEqual([['first', 'Heat to 93 degrees.'], ['third', 'Done.']]);
    expect(items[0].key).toMatch(/^[0-9a-f]{16}$/);
    expect(again.map(item => item.key)).toEqual(items.map(item => item.key));
  });

  it('WhenVoiceChanges_ThenKeysChange', () => {
    // Act
    const items = buildNarration({...script, voice: 'kokoro:af_heart'}, lexicon);

    // Assert
    expect(items[0].key).not.toBe(buildNarration(script, lexicon)[0].key);
  });

  it('WhenLexiconHasEngineOverride_ThenTheScriptVoiceEngineSelectsTheSpokenForm', () => {
    // Arrange
    const overridden = [{match: '°C', say: 'degrees', kokoro: 'degrees Celsius'}];

    // Act
    const kokoro = buildNarration({...script, voice: 'kokoro:am_michael'}, overridden);
    const chatterbox = buildNarration(script, overridden);

    // Assert
    expect(kokoro[0].text).toBe('Heat to 93 degrees Celsius.');
    expect(chatterbox[0].text).toBe('Heat to 93 degrees.');
  });
});

describe('tempoKey', () => {
  it('WhenTempoIsOne_ThenKeyIsTheSynthesisKey', () => {
    // Act & Assert
    expect(tempoKey('0123456789abcdef', 1)).toBe('0123456789abcdef');
  });

  it('WhenTempoDiffers_ThenKeyNamesTheTempo', () => {
    // Act
    const key = tempoKey('0123456789abcdef', 1.1);

    // Assert
    expect(key).toBe('0123456789abcdef-x1.1');
    expect(tempoKey('0123456789abcdef', 1.15)).not.toBe(key);
  });
});

describe('buildNarration tempo', () => {
  it('WhenTempoIsDefault_ThenAudioKeysAreTheSynthesisKeys', () => {
    // Act
    const items = buildNarration(script, lexicon);

    // Assert
    expect(script.tempo).toBe(1);
    expect(items[0].audioKey).toBe(items[0].key);
  });

  it('WhenTempoChanges_ThenSynthesisKeysStayAndAudioKeysChange', () => {
    // Act
    const items = buildNarration(script, lexicon);
    const faster = buildNarration({...script, tempo: 1.2}, lexicon);

    // Assert
    expect(faster.map(item => item.key)).toEqual(items.map(item => item.key));
    expect(faster[0].audioKey).toBe(`${items[0].key}-x1.2`);
  });
});

describe('buildNarration tempo with Kokoro', () => {
  it('WhenKokoroTempoChanges_ThenSynthesisKeysChangeAndAudioIsTheSynthesizedFile', () => {
    // Arrange
    const kokoro = {...script, voice: 'kokoro:am_michael'};

    // Act
    const natural = buildNarration(kokoro, lexicon);
    const faster = buildNarration({...kokoro, tempo: 1.2}, lexicon);

    // Assert
    expect(faster[0].key).not.toBe(natural[0].key);
    expect(faster.map(item => item.audioKey)).toEqual(faster.map(item => item.key));
    expect(faster[0].key).toBe(buildNarration({...kokoro, tempo: 1.2}, lexicon)[0].key);
    expect(buildNarration({...kokoro, tempo: 1.15}, lexicon)[0].key).not.toBe(faster[0].key);
  });

  it('WhenKokoroTempoExceedsItsMaximumSpeed_ThenSpeechAtTheMaximumGetsAnAtempoCopy', () => {
    // Arrange
    const kokoro = {...script, voice: 'kokoro:am_michael'};

    // Act
    const maximum = buildNarration({...kokoro, tempo: 1.25}, lexicon);
    const faster = buildNarration({...kokoro, tempo: 1.32}, lexicon);

    // Assert
    expect(faster.map(item => item.key)).toEqual(maximum.map(item => item.key));
    expect(faster[0].audioKey).toBe(`${maximum[0].key}-x1.056`);
  });
});

describe('buildTiming', () => {
  it('WhenDurationsAreKnown_ThenBeatsAreLaidOutBackToBack', () => {
    // Arrange
    const items = buildNarration(script, lexicon);
    const durations = {[items[0].audioKey]: 3, [items[1].audioKey]: 1};

    // Act
    const timing = buildTiming(script, items, durations);

    // Assert
    expect(timing.beats).toEqual([
      {id: 'first', chapter: 'one', chapterTitle: 'One', start: 0, duration: 3 + narrationPadding, audio: `/generated/smoke/audio/${items[0].audioKey}.wav`, caption: 'Heat to 93 °C.'},
      {id: 'second', chapter: 'one', chapterTitle: 'One', start: 3 + narrationPadding, duration: 2, audio: null, caption: null},
      {id: 'third', chapter: 'two', chapterTitle: 'Two', start: 5 + narrationPadding, duration: 1 + narrationPadding + 0.5, audio: `/generated/smoke/audio/${items[1].audioKey}.wav`, caption: 'Done.'},
    ]);
    expect(timing.totalDuration).toBeCloseTo(6.5 + 2 * narrationPadding);
    expect(timing.title).toBe('Smoke');
  });

  it('WhenOnlyTheSynthesizedDurationIsKnown_ThenThrows', () => {
    // Arrange
    const faster = {...script, tempo: 1.1};
    const items = buildNarration(faster, lexicon);
    const durations = {[items[0].key]: 3, [items[1].key]: 1};

    // Act & Assert
    expect(() => buildTiming(faster, items, durations)).toThrow(/No audio duration for beat 'first'/);
  });

  it('WhenDurationIsMissing_ThenThrows', () => {
    // Arrange
    const items = buildNarration(script, lexicon);

    // Act & Assert
    expect(() => buildTiming(script, items, {})).toThrow(/No audio duration for beat 'first'/);
  });
});

describe('applyNarrationOptions', () => {
  it('WhenNoOverrideIsGiven_ThenScriptAndRegularTimingAreKept', () => {
    // Act
    const result = applyNarrationOptions(script, ['--final', '--beats', 'first']);

    // Assert
    expect(result).toEqual({script, variant: '', timingFileName: 'timing.json'});
  });

  it('WhenVoiceAndTempoAreOverridden_ThenVariantGetsItsOwnTiming', () => {
    // Act
    const result = applyNarrationOptions(script, ['--voice', 'chatterbox-calm', '--tempo', '1.05']);

    // Assert
    expect(result.script.voice).toBe('chatterbox-calm');
    expect(result.script.tempo).toBe(1.05);
    expect(result.variant).toBe('chatterbox-calm-x1.05');
    expect(result.timingFileName).toBe('timing-chatterbox-calm-x1.05.json');
  });

  it('WhenOnlyVoiceIsOverridden_ThenScriptTempoIsKept', () => {
    // Act
    const result = applyNarrationOptions(script, ['--voice', 'kokoro:af_heart']);

    // Assert
    expect(result.variant).toBe('kokoro-af_heart-x1');
    expect(buildNarration(result.script, lexicon)[0].key).toBe(buildNarration({...script, voice: 'kokoro:af_heart'}, lexicon)[0].key);
  });

  it('WhenOverrideIsInvalid_ThenThrows', () => {
    // Act & Assert
    expect(() => applyNarrationOptions(script, ['--tempo', '3'])).toThrow(/--tempo must be a number between 0.5 and 2/);
    expect(() => applyNarrationOptions(script, ['--voice', 'robot'])).toThrow(/Unknown voice 'robot'/);
    expect(() => applyNarrationOptions(script, ['--voice', '--final'])).toThrow(/--voice needs a value/);
  });
});

describe('toSrt', () => {
  it('WhenTimingHasCaptions_ThenWritesNumberedCues', () => {
    // Arrange
    const timing = {
      episode: 'smoke',
      title: 'Smoke',
      totalDuration: 5,
      beats: [
        {id: 'a', chapter: 'one', chapterTitle: 'One', start: 0, duration: 1.25, audio: null, caption: 'Hello.'},
        {id: 'b', chapter: 'one', chapterTitle: 'One', start: 1.25, duration: 2, audio: null, caption: null},
        {id: 'c', chapter: 'one', chapterTitle: 'One', start: 3.25, duration: 61, audio: null, caption: 'Later.'},
      ],
    };

    // Act
    const srt = toSrt(timing);

    // Assert
    expect(srt).toBe('1\n00:00:00,000 --> 00:00:01,250\nHello.\n\n2\n00:00:03,250 --> 00:01:04,250\nLater.\n');
  });
});
