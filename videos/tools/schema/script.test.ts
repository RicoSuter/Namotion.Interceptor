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
    expect(script.voice).toBe('default');
    expect(allBeats(script).map(beat => [beat.chapter, beat.id])).toEqual([['basics', 'hello'], ['basics', 'pause']]);
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

  it('WhenBeatIdIsNotKebabCase_ThenThrows', () => {
    // Arrange
    const yaml = validScript.replace('id: hello', 'id: Hello_World');

    // Act & Assert
    expect(() => parseScript(yaml)).toThrow(/kebab-case/);
  });
});
