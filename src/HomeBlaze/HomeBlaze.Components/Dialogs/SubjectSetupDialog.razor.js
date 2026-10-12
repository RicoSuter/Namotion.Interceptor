export function hasFinePointer() {
    return window.matchMedia('(hover: hover) and (pointer: fine)').matches;
}
