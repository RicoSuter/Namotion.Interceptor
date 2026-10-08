import {describe, expect, it} from 'vitest';
import {clearOfHeader, connectionSides, headerZone, messageCurve, routeEdges, scrollToFocus, sideCurve, type Box} from './geometry';

const box = (x: number, y: number): Box => ({x, y, width: 200, height: 100});

describe('connectionSides', () => {
  it('WhenTargetIsRightOfSource_ThenEdgeLeavesRightAndEntersLeft', () => {
    // Act
    const sides = connectionSides(box(0, 0), box(400, 60));

    // Assert
    expect(sides).toEqual({source: 'right', target: 'left'});
  });

  it('WhenTargetIsBelowSource_ThenEdgeLeavesBottomAndEntersTop', () => {
    // Act
    const sides = connectionSides(box(0, 0), box(50, 300));

    // Assert
    expect(sides).toEqual({source: 'bottom', target: 'top'});
  });

  it('WhenLayoutIsTopDownAndTargetIsInALowerLayer_ThenEdgeIsVerticalEvenWithAWiderGapSideways', () => {
    // Act
    const sides = connectionSides(box(0, 0), box(600, 250), 'down');

    // Assert
    expect(sides).toEqual({source: 'bottom', target: 'top'});
  });

  it('WhenTargetIsLeftOfSource_ThenEdgeLeavesLeftAndEntersRight', () => {
    // Act
    const sides = connectionSides(box(400, 0), box(0, 0));

    // Assert
    expect(sides).toEqual({source: 'left', target: 'right'});
  });
});

describe('sideCurve', () => {
  it('WhenSidesAreHorizontal_ThenHandlesAreHorizontalSoTheCurveHasNoCorners', () => {
    // Act
    const curve = sideCurve({x: 100, y: 0}, 'right', {x: 300, y: 80}, 'left');

    // Assert
    expect(curve.p1).toEqual({x: 200, y: 0});
    expect(curve.p2).toEqual({x: 200, y: 80});
  });

  it('WhenEndsAreClose_ThenHandlesKeepMinimumLength', () => {
    // Act
    const curve = sideCurve({x: 0, y: 0}, 'bottom', {x: 0, y: 20}, 'top');

    // Assert
    expect(curve.p1).toEqual({x: 0, y: 48});
    expect(curve.p2).toEqual({x: 0, y: -28});
  });
});

describe('routeEdges', () => {
  it('WhenTwoEdgesShareASide_ThenAnchorsAreSpreadInOrderOfTheirTargets', () => {
    // Arrange
    const boxes = new Map([['a', box(0, 0)], ['b', box(400, -150)], ['c', box(400, 150)]]);

    // Act
    const [toB, toC] = routeEdges(boxes, [{from: 'a', to: 'b'}, {from: 'a', to: 'c'}]);

    // Assert
    expect(toB.p0).toEqual({x: 100, y: -30});
    expect(toC.p0).toEqual({x: 100, y: 30});
    expect(toB.p3).toEqual({x: 300, y: -150});
  });

  it('WhenEdgeRefersToUnknownNode_ThenThrows', () => {
    // Act & Assert
    expect(() => routeEdges(new Map([['a', box(0, 0)]]), [{from: 'a', to: 'x'}])).toThrow("unknown node 'x'");
  });
});

describe('messageCurve', () => {
  it('WhenLifelinesDiffer_ThenCurveBowsUpwardBetweenThem', () => {
    // Act
    const curve = messageCurve(0, 400, 100);

    // Assert
    expect(curve.p0).toEqual({x: 0, y: 100});
    expect(curve.p3).toEqual({x: 400, y: 100});
    expect(curve.p1.y).toBe(64);
  });

  it('WhenLifelineIsTheSame_ThenCurveLoopsToTheRight', () => {
    // Act
    const curve = messageCurve(200, 200, 100);

    // Assert
    expect(curve.p1.x).toBeGreaterThan(200);
    expect(curve.p3.y).toBeGreaterThan(curve.p0.y);
  });
});

describe('scrollToFocus', () => {
  it('WhenFocusIsVisible_ThenScrollIsUnchanged', () => {
    // Act
    const scroll = scrollToFocus(400, 1000, 100, 200, 50);

    // Assert
    expect(scroll).toBe(50);
  });

  it('WhenFocusIsBelowViewport_ThenFocusIsCentered', () => {
    // Act
    const scroll = scrollToFocus(400, 1000, 600, 700);

    // Assert
    expect(scroll).toBe(450);
  });

  it('WhenCenteringPassesContentEnd_ThenScrollIsClamped', () => {
    // Act
    const scroll = scrollToFocus(400, 1000, 900, 980);

    // Assert
    expect(scroll).toBe(600);
  });

  it('WhenContentFits_ThenScrollIsZero', () => {
    // Act
    const scroll = scrollToFocus(400, 300, 250, 290, 20);

    // Assert
    expect(scroll).toBe(0);
  });
});

describe('clearOfHeader', () => {
  it('WhenContentReachesIntoTheHeaderZone_ThenMovesDownJustEnough', () => {
    // Act
    const position = clearOfHeader(-400, 620, 1.2, {x: 0, y: 0});

    // Assert
    expect(position.x).toBe(0);
    expect(-400 * 1.2 + position.y).toBeCloseTo(headerZone.bottom);
  });

  it('WhenContentStaysBelowTheZone_ThenPositionIsUnchanged', () => {
    // Act
    const position = clearOfHeader(-390, 620, 1, {x: 0, y: 20});

    // Assert
    expect(position).toEqual({x: 0, y: 20});
  });

  it('WhenContentEndsLeftOfTheZone_ThenPositionIsUnchanged', () => {
    // Act
    const position = clearOfHeader(-500, 200, 1.2, {x: 0, y: 0});

    // Assert
    expect(position).toEqual({x: 0, y: 0});
  });
});
