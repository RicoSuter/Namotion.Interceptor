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

let isForwardingPreviewClicks = false;

// The content of the preview next to the editor is inert, so a click lands on the preview itself. It goes on to the
// revealable widget under the pointer, which reveals its block in the editor. While the content is not inert for this one
// call, the pointer-events rules of the wrappers make elementFromPoint find the innermost revealable widget.
export function forwardPreviewClicks() {
    if (isForwardingPreviewClicks) {
        return;
    }

    isForwardingPreviewClicks = true;
    document.addEventListener('click', event => {
        const preview = event.target;
        if (!(preview instanceof Element) || !preview.classList.contains('page-split-preview')) {
            return;
        }

        const content = preview.querySelector(':scope > .page-split-preview-content');
        if (!content) {
            return;
        }

        content.inert = false;
        const element = document.elementFromPoint(event.clientX, event.clientY);
        content.inert = true;

        const wrapper = element?.closest('.subject-component-wrapper.revealable');
        if (wrapper && content.contains(wrapper)) {
            wrapper.dispatchEvent(new MouseEvent('click', {
                bubbles: true,
                cancelable: true,
                clientX: event.clientX,
                clientY: event.clientY
            }));
        }
    });
}
