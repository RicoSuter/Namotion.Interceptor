import {describe, expect, it} from 'vitest';
import {allBeats, parseScript} from './script';

const validScript = `
episode: smoke
title: Smoke test
chapters:
  - id: basics
    title: Basics
    beats:
      - id: hello
        narration: Hello there.
        visual: Title card fades in.
        components: [ChapterCard]
      - id: pause
        hold: 1.5
        visual: Code card settles.
        components: [CodeCard]
        code: {file: ../../domain/Coffee/Boiler.cs, region: Boiler}
`;

describe('parseScript', () => {
  it('WhenScriptIsValid_ThenDefaultsVoiceAndKeepsBeats', () => {
    // Act
    const script = parseScript(validScript);

    // Assert
    expect(script.voice).toBe('chatterbox');
    expect(allBeats(script).map(beat => [beat.chapter, beat.id])).toEqual([['basics', 'hello'], ['basics', 'pause']]);
  });

  it('WhenTempoIsSet_ThenKeepsItOtherwiseDefaults', () => {
    // Act
    const script = parseScript(validScript.replace('title: Smoke test\n', 'title: Smoke test\ntempo: 1.05\n'));

    // Assert
    expect(script.tempo).toBe(1.05);
    expect(parseScript(validScript).tempo).toBe(1);
  });

  it('WhenBackgroundIsKnown_ThenKeepsItAndRejectsOthers', () => {
    // Act
    const script = parseScript(validScript.replace('title: Smoke test\n', 'title: Smoke test\nbackground: edge-aurora\n'));

    // Assert
    expect(script.background).toBe('edge-aurora');
    expect(parseScript(validScript).background).toBeUndefined();
    expect(() => parseScript(validScript.replace('title: Smoke test\n', 'title: Smoke test\nbackground: plaid\n'))).toThrow(/Invalid script.yaml/);
  });

  it('WhenVoiceIsUnknown_ThenThrowsWithTheVoiceError', () => {
    // Arrange
    const yaml = validScript.replace('title: Smoke test\n', 'title: Smoke test\nvoice: kokoro:xx\n');

    // Act & Assert
    expect(() => parseScript(yaml)).toThrow(/Unknown Kokoro voice 'xx'/);
  });

  it('WhenTempoIsOutOfRange_ThenThrows', () => {
    // Arrange
    const yaml = validScript.replace('title: Smoke test\n', 'title: Smoke test\ntempo: 3\n');

    // Act & Assert
    expect(() => parseScript(yaml)).toThrow(/Invalid script.yaml/);
  });

  it('WhenBeatHasNeitherNarrationNorHold_ThenThrows', () => {
    // Arrange
    const yaml = validScript.replace('        hold: 1.5\n', '');

    // Act & Assert
    expect(() => parseScript(yaml)).toThrow(/narration, hold, or both/);
  });

  it('WhenBeatIdIsDuplicated_ThenThrows', () => {
    // Arrange
    const yaml = validScript.replace('id: pause', 'id: hello');

    // Act & Assert
    expect(() => parseScript(yaml)).toThrow(/Duplicate beat id 'hello'/);
  });

  it('WhenComponentIsUnknown_ThenThrows', () => {
    // Arrange
    const yaml = validScript.replace('[ChapterCard]', '[BulletSlide]');

    // Act & Assert
    expect(() => parseScript(yaml)).toThrow(/Invalid script.yaml/);
  });

  it('WhenBeatListsCaption_ThenThrows', () => {
    // Arrange
    const yaml = validScript.replace('[ChapterCard]', '[ChapterCard, Caption]');

    // Act & Assert
    expect(() => parseScript(yaml)).toThrow(/Invalid script.yaml/);
  });

  it('WhenBeatIdIsNotKebabCase_ThenThrows', () => {
    // Arrange
    const yaml = validScript.replace('id: hello', 'id: Hello_World');

    // Act & Assert
    expect(() => parseScript(yaml)).toThrow(/kebab-case/);
  });
});
