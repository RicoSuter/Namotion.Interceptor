import {describe, expect, it} from 'vitest';
import {synthesisKey} from './narration';
import {parseVoice, voiceIdentity, voiceLabel, voiceRequest} from './voice';

describe('parseVoice', () => {
  it('WhenSpecNamesChatterbox_ThenUsesItsPreset', () => {
    // Act & Assert
    expect(parseVoice('chatterbox')).toEqual({engine: 'chatterbox', preset: 'default'});
    expect(parseVoice('chatterbox-calm')).toEqual({engine: 'chatterbox', preset: 'calm'});
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
    const labels = ['chatterbox', 'chatterbox-calm', 'kokoro:af_heart'].map(spec => voiceLabel(parseVoice(spec)));

    // Assert
    expect(labels).toEqual(['chatterbox-default', 'chatterbox-calm', 'kokoro-af_heart']);
  });
});

describe('voiceRequest', () => {
  it('WhenVoiceIsChatterboxPreset_ThenRequestCarriesItsSettings', () => {
    // Act & Assert
    expect(voiceRequest(parseVoice('chatterbox'))).toEqual({engine: 'chatterbox', reference: null, exaggeration: 0.5, cfgWeight: 0.5});
    expect(voiceRequest(parseVoice('chatterbox-calm'))).toEqual({engine: 'chatterbox', reference: null, exaggeration: 0.35, cfgWeight: 0.3});
    expect(voiceRequest(parseVoice('kokoro:af_heart'))).toEqual({engine: 'kokoro', name: 'af_heart'});
  });
});

describe('voiceIdentity', () => {
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
});
