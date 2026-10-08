/** Background layers the scene runtime can draw behind all content. */
export const backgroundVariants = ['drift', 'chapter-tint', 'edge-aurora', 'follow-light'] as const;

export type BackgroundVariant = (typeof backgroundVariants)[number];

/** Background of every episode that does not choose one in script.yaml. */
export const defaultBackground: BackgroundVariant = 'drift';
