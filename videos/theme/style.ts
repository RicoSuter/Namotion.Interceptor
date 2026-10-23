import {easeInOutCubic, easeOutCubic, makeSpring, type Spring} from '@revideo/core';

export const radius = {card: 24, small: 12, pill: 999} as const;

/** Soft drop shadow used instead of outlines. */
export const shadow = {shadowColor: 'rgba(0, 0, 0, 0.45)', shadowBlur: 40, shadowOffsetY: 12} as const;

/** Smaller shadow for elements that sit on a card. */
export const smallShadow = {shadowColor: 'rgba(0, 0, 0, 0.35)', shadowBlur: 16, shadowOffsetY: 4} as const;

export const fonts = {text: 'Inter', code: 'JetBrains Mono'} as const;

export const fontSize = {label: 30, detail: 22, code: 30, terminal: 30, title: 112, kicker: 28, header: 22} as const;

/** Distance of frame overlays such as the chapter header from the frame edges (title safe area at 1080p). */
export const safeMargin = {x: 96, y: 54} as const;

export const durations = {fast: 0.3, normal: 0.6, slow: 1.2} as const;

/** Camera and layout moves. */
export const moveEasing = easeInOutCubic;

/** Elements fading or sliding in. */
export const enterEasing = easeOutCubic;

/** Spring for arriving elements: settles quickly with a slight overshoot. */
export const arrivalSpring: Spring = makeSpring(1, 170, 18);

