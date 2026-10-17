const widthProperty = '--editor-bar-width';

// Keeps the width of the bar above the editor in a CSS property of the content of the page head, whose container
// queries leave out what does not fit into the bar. The bar changes its width with the window, the navigation drawer
// and the splitter. Returns a handle that stops it.
export function fitToEditorBar(content, bar) {
    // An element is gone when the page left the mode while this module loaded.
    if (!content || !bar) {
        return { dispose: () => { } };
    }

    const observer = new ResizeObserver(() => {
        const value = `${Math.round(bar.getBoundingClientRect().width)}px`;
        if (content.style.getPropertyValue(widthProperty) !== value) {
            content.style.setProperty(widthProperty, value);
        }
    });
    observer.observe(bar);

    return {
        dispose: () => {
            observer.disconnect();
            content.style.removeProperty(widthProperty);
        }
    };
}
