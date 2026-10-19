// Reports the width of the frame of a canvas that fits the width, at once and whenever it changes.
export function observeWidth(element, dotNetReference) {
    const observer = new ResizeObserver(entries => {
        const width = entries[entries.length - 1].contentRect.width;
        // A disposed reference or a lost circuit only means that nobody listens anymore.
        dotNetReference.invokeMethodAsync('OnFrameWidthChanged', width).catch(() => {});
    });
    observer.observe(element);
    return { dispose: () => observer.disconnect() };
}
