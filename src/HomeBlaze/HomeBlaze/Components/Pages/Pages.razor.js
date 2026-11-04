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

// Whether the browser runs on a Mac, an iPad or an iPhone, whose shortcuts use the Command key instead of Control.
export function isApplePlatform() {
    const platform = navigator.userAgentData?.platform ?? navigator.platform ?? '';
    return /mac|iphone|ipad|ipod/i.test(platform);
}

let isForwardingPreviewPointer = false;

// Gets the innermost revealable widget of a preview at a point of the viewport, or null. Its content is inert, so the
// browser does not hit-test it, and the bounds of the wrappers are compared instead. A widget inside another one comes
// after it in document order, so the last wrapper that contains the point is the innermost.
function findRevealableWrapper(preview, x, y) {
    const content = preview.querySelector(':scope > .page-split-preview-content');
    if (!content) {
        return null;
    }

    let found = null;
    for (const wrapper of content.querySelectorAll('.subject-component-wrapper.revealable')) {
        const bounds = wrapper.getBoundingClientRect();
        if (x >= bounds.left && x < bounds.right && y >= bounds.top && y < bounds.bottom) {
            found = wrapper;
        }
    }

    return found;
}

function isPreview(target) {
    return target instanceof Element && target.classList.contains('page-split-preview');
}

// The content of the preview next to the editor is inert, so the pointer lands on the preview itself. A click goes on to
// the revealable widget under the pointer, which reveals its block in the editor, and while the pointer moves, that widget
// shows that a click reveals it.
export function forwardPreviewPointer() {
    if (isForwardingPreviewPointer) {
        return;
    }

    isForwardingPreviewPointer = true;

    let hoveredPreview = null;
    let hoveredWrapper = null;
    let pendingMove = null;

    // The cursor is the one of the preview, because the browser takes it from the element under the pointer, and the
    // wrappers inside the inert content are never that element.
    const showHover = (preview, wrapper) => {
        if (hoveredWrapper !== wrapper) {
            hoveredWrapper?.classList.remove('reveal-hover');
        }

        if (hoveredPreview !== preview) {
            hoveredPreview?.classList.remove('reveal-hover');
        }

        // Added on every move, because a render of the wrapper that changes its class attribute drops the class.
        wrapper?.classList.add('reveal-hover');
        preview?.classList.toggle('reveal-hover', wrapper !== null);
        hoveredWrapper = wrapper;
        hoveredPreview = preview;
    };

    document.addEventListener('click', event => {
        const preview = event.target;
        if (!isPreview(preview)) {
            return;
        }

        const wrapper = findRevealableWrapper(preview, event.clientX, event.clientY);
        wrapper?.dispatchEvent(new MouseEvent('click', {
            bubbles: true,
            cancelable: true,
            clientX: event.clientX,
            clientY: event.clientY
        }));
    });

    document.addEventListener('pointermove', event => {
        if (!isPreview(event.target)) {
            // A preview that left the page while the pointer was over it got no pointerout.
            if (hoveredPreview !== null) {
                pendingMove = null;
                showHover(null, null);
            }

            return;
        }

        const isFramePending = pendingMove !== null;
        pendingMove = event;
        if (isFramePending) {
            return;
        }

        requestAnimationFrame(() => {
            const move = pendingMove;
            if (move === null) {
                return;
            }

            pendingMove = null;
            const preview = move.target.isConnected ? move.target : null;
            showHover(preview, preview ? findRevealableWrapper(preview, move.clientX, move.clientY) : null);
        });
    });

    // pointerleave does not bubble to the document. pointerout on the preview is its leave, because its content is inert.
    document.addEventListener('pointerout', event => {
        if (isPreview(event.target)) {
            pendingMove = null;
            showHover(null, null);
        }
    });
}
