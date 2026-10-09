import {describe, expect, it} from 'vitest';
import {synthesisKey} from './narration';
import {kokoroMaximumSpeed, parseVoice, tempoPlan, voiceIdentity, voiceLabel, voiceRequest} from './voice';

describe('parseVoice', () => {
  it('WhenSpecNamesChatterbox_ThenUsesItsPreset', () => {
    // Act & Assert
    expect(parseVoice('chatterbox')).toEqual({engine: 'chatterbox', preset: 'default', reference: null});
    expect(parseVoice('chatterbox-calm')).toEqual({engine: 'chatterbox', preset: 'calm', reference: null});
  });

  it('WhenSpecNamesClone_ThenChatterboxClonesTheReference', () => {
    // Act & Assert
    expect(parseVoice('clone:voices/rico.m4a')).toEqual({engine: 'chatterbox', preset: 'default', reference: 'voices/rico.m4a'});
    expect(parseVoice('clone-calm:voices/rico.wav')).toEqual({engine: 'chatterbox', preset: 'calm', reference: 'voices/rico.wav'});
    expect(() => parseVoice('clone:')).toThrow(/Unknown voice 'clone:'/);
  });

  it('WhenSpecNamesKokoroVoice_ThenKeepsTheName', () => {
    // Act
    const voice = parseVoice('kokoro:bm_george');

    // Assert
    expect(voice).toEqual({engine: 'kokoro', name: 'bm_george'});
  });

  it('WhenKokoroVoiceIsNotEnglish_ThenThrows', () => {
    // Act & Assert
    expect(() => parseVoice('kokoro:jf_alpha')).toThrow(/Unknown Kokoro voice 'jf_alpha'/);
    expect(() => parseVoice('kokoro:')).toThrow(/Unknown Kokoro voice/);
  });

  it('WhenSpecIsUnknown_ThenThrows', () => {
    // Act & Assert
    expect(() => parseVoice('default')).toThrow(/Unknown voice 'default'/);
  });
});

describe('voiceLabel', () => {
  it('WhenVoicesDiffer_ThenLabelsAreFileNameSafeAndDistinct', () => {
    // Act
    const labels = ['chatterbox', 'chatterbox-calm', 'kokoro:af_heart', 'clone:voices/rico.m4a', 'clone-calm:/home/me/My Voice.wav'].map(spec => voiceLabel(parseVoice(spec)));

    // Assert
    expect(labels).toEqual(['chatterbox-default', 'chatterbox-calm', 'kokoro-af_heart', 'clone-rico', 'clone-My-Voice-calm']);
  });
});

describe('voiceRequest', () => {
  it('WhenVoiceIsChatterboxPreset_ThenRequestCarriesItsSettings', () => {
    // Act & Assert
    expect(voiceRequest(parseVoice('chatterbox'))).toEqual({engine: 'chatterbox', reference: null, exaggeration: 0.5, cfgWeight: 0.5});
    expect(voiceRequest(parseVoice('chatterbox-calm'))).toEqual({engine: 'chatterbox', reference: null, exaggeration: 0.35, cfgWeight: 0.3});
    expect(voiceRequest(parseVoice('kokoro:af_heart'))).toEqual({engine: 'kokoro', name: 'af_heart', speed: 1});
  });

  it('WhenKokoroHasSpeed_ThenRequestCarriesIt', () => {
    // Act & Assert
    expect(voiceRequest(parseVoice('kokoro:am_michael'), null, 1.34)).toEqual({engine: 'kokoro', name: 'am_michael', speed: 1.34});
    expect(() => voiceRequest(parseVoice('chatterbox'), null, 1.2)).toThrow(/has no native speed/);
  });

  it('WhenVoiceIsClone_ThenRequestNeedsThePreparedReference', () => {
    // Arrange
    const voice = parseVoice('clone-calm:voices/rico.m4a');

    // Act & Assert
    expect(voiceRequest(voice, '/videos/voices/.prepared/0123.wav')).toEqual({engine: 'chatterbox', reference: '/videos/voices/.prepared/0123.wav', exaggeration: 0.35, cfgWeight: 0.3});
    expect(() => voiceRequest(voice)).toThrow(/needs its prepared reference/);
  });
});

describe('tempoPlan', () => {
  it('WhenVoiceIsKokoro_ThenEngineReachesTheTempoNatively', () => {
    // Act & Assert
    expect(tempoPlan(parseVoice('kokoro:am_michael'), 1.2)).toEqual({speed: 1.2, atempo: 1});
    expect(tempoPlan(parseVoice('kokoro:am_michael'), 1)).toEqual({speed: 1, atempo: 1});
    expect(tempoPlan(parseVoice('kokoro:am_michael'), 0.8)).toEqual({speed: 0.8, atempo: 1});
  });

  it('WhenKokoroTempoExceedsItsMaximumSpeed_ThenAtempoAddsTheRest', () => {
    // Act & Assert
    expect(tempoPlan(parseVoice('kokoro:am_michael'), kokoroMaximumSpeed)).toEqual({speed: kokoroMaximumSpeed, atempo: 1});
    expect(tempoPlan(parseVoice('kokoro:am_michael'), 1.32)).toEqual({speed: 1.25, atempo: 1.056});
    expect(tempoPlan(parseVoice('kokoro:am_michael'), 2)).toEqual({speed: 1.25, atempo: 1.6});
  });

  it('WhenVoiceIsChatterboxOrClone_ThenAtempoReachesTheTempo', () => {
    // Act & Assert
    expect(tempoPlan(parseVoice('chatterbox'), 1.2)).toEqual({speed: 1, atempo: 1.2});
    expect(tempoPlan(parseVoice('clone-calm:voices/rico.wav'), 0.9)).toEqual({speed: 1, atempo: 0.9});
  });
});

describe('voiceIdentity', () => {
  it('WhenKokoroSpeedChanges_ThenKeyChangesAndSpeedOneKeepsTheEarlierKey', () => {
    // Arrange
    const voice = parseVoice('kokoro:am_michael');

    // Act
    const natural = synthesisKey('Hello.', voiceIdentity(voice));
    const faster = synthesisKey('Hello.', voiceIdentity(voice, null, 1.3));

    // Assert
    expect(natural).toBe(synthesisKey('Hello.', {voice: {name: 'am_michael'}, engine: 'kokoro-1'}));
    expect(synthesisKey('Hello.', voiceIdentity(voice, null, 1))).toBe(natural);
    expect(faster).not.toBe(natural);
    expect(synthesisKey('Hello.', voiceIdentity(voice, null, 1.34))).not.toBe(faster);
  });

  it('WhenChatterboxGetsASpeed_ThenThrows', () => {
    // Act & Assert
    expect(() => voiceIdentity(parseVoice('chatterbox'), null, 1.2)).toThrow(/has no native speed/);
  });

  it('WhenVoiceIsDefaultChatterbox_ThenKeyMatchesAudioCachedBeforeVoicesWereConfigurable', () => {
    // Act
    const key = synthesisKey('Hello.', voiceIdentity(parseVoice('chatterbox')));

    // Assert
    expect(key).toBe(synthesisKey('Hello.', {voice: 'default', engine: 'chatterbox-1'}));
  });

  it('WhenEngineOrSettingsDiffer_ThenKeysDiffer', () => {
    // Act
    const keys = ['chatterbox', 'chatterbox-calm', 'kokoro:af_heart', 'kokoro:am_michael'].map(spec => synthesisKey('Hello.', voiceIdentity(parseVoice(spec))));

    // Assert
    expect(new Set(keys).size).toBe(4);
  });

  it('WhenCloneReferenceOrSettingsChange_ThenKeysChange', () => {
    // Arrange
    const clone = parseVoice('clone:voices/rico.m4a');

    // Act
    const key = synthesisKey('Hello.', voiceIdentity(clone, 'aaaa'));

    // Assert
    expect(synthesisKey('Hello.', voiceIdentity(parseVoice('clone:voices/other-name.m4a'), 'aaaa'))).toBe(key);
    expect(synthesisKey('Hello.', voiceIdentity(clone, 'bbbb'))).not.toBe(key);
    expect(synthesisKey('Hello.', voiceIdentity(parseVoice('clone-calm:voices/rico.m4a'), 'aaaa'))).not.toBe(key);
    expect(synthesisKey('Hello.', voiceIdentity(parseVoice('chatterbox')))).not.toBe(key);
    expect(() => voiceIdentity(clone)).toThrow(/needs the digest/);
  });
});
