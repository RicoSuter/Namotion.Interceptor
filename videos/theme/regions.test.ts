import {describe, expect, it} from 'vitest';
import {extractRegion} from './regions';

const source = [
  'namespace Coffee;',
  '',
  '    #region Boiler',
  '    public partial class Boiler',
  '    {',
  '        #region Temperature',
  '        public partial double Temperature { get; set; }',
  '        #endregion',
  '    }',
  '    #endregion',
].join('\n');

describe('extractRegion', () => {
  it('WhenRegionExists_ThenReturnsDedentedBodyWithoutNestedMarkers', () => {
    // Act
    const code = extractRegion(source, 'Boiler');

    // Assert
    expect(code).toBe(['public partial class Boiler', '{', '    public partial double Temperature { get; set; }', '}'].join('\n'));
  });

  it('WhenRegionIsNested_ThenReturnsOnlyItsBody', () => {
    // Act
    const code = extractRegion(source, 'Temperature');

    // Assert
    expect(code).toBe('public partial double Temperature { get; set; }');
  });

  it('WhenSourceUsesCrLf_ThenRegionIsFound', () => {
    // Act
    const code = extractRegion(source.replaceAll('\n', '\r\n'), 'Temperature');

    // Assert
    expect(code).toBe('public partial double Temperature { get; set; }');
  });

  it('WhenRegionIsMissing_ThenThrows', () => {
    // Act & Assert
    expect(() => extractRegion(source, 'Pump')).toThrow("Region 'Pump' not found");
  });

  it('WhenRegionIsNotClosed_ThenThrows', () => {
    // Arrange
    const unterminated = '#region Open\nint x;';

    // Act & Assert
    expect(() => extractRegion(unterminated, 'Open')).toThrow("Region 'Open' has no matching #endregion");
  });
});
