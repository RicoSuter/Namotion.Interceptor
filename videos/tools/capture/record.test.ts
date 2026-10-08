import {describe, expect, it} from 'vitest';
import {concatList} from './record';

describe('concatList', () => {
  it('WhenFramesAreCaptured_ThenEachLastsUntilTheNextAndTheLastIsRepeated', () => {
    // Act
    const list = concatList([{file: 'a.jpg', time: 0}, {file: 'b.jpg', time: 50}]);

    // Assert
    expect(list).toBe("file 'a.jpg'\nduration 0.0500\nfile 'b.jpg'\nduration 0.0333\nfile 'b.jpg'\n");
  });
});
