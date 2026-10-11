// Sends the chat message on Enter where a hardware keyboard is likely, that is with a fine pointer such as a mouse.
// On touch devices Enter keeps inserting a line break and the Send button sends. Shift+Enter keeps the default line
// break, and Enter that confirms an IME composition is left to the input method.
const finePointer = window.matchMedia('(pointer: fine)');

export function attachEnterToSend(container, dotNetReference) {
    const textarea = container?.querySelector('textarea');
    if (!textarea) {
        return null;
    }

    const onKeyDown = event => {
        if (event.key !== 'Enter' || event.shiftKey || event.isComposing || event.keyCode === 229 || !finePointer.matches) {
            return;
        }

        event.preventDefault();
        dotNetReference.invokeMethodAsync('SendFromKeyboardAsync', textarea.value);
    };

    // A hybrid device switches pointers when a keyboard or mouse is attached, so the keyboard hint follows.
    const onPointerChange = () => dotNetReference.invokeMethodAsync('SetSendsOnEnter', finePointer.matches);

    textarea.addEventListener('keydown', onKeyDown);
    finePointer.addEventListener('change', onPointerChange);
    return {
        sendsOnEnter: () => finePointer.matches,
        dispose: () => {
            textarea.removeEventListener('keydown', onKeyDown);
            finePointer.removeEventListener('change', onPointerChange);
        }
    };
}

// Keeps the timeline scrolled to the newest content while the reader is at the bottom. The reader scrolling up
// releases it, so new content never moves the view; scrolling back to the bottom or pin() holds it again. Clicking
// inside an element marked data-keeps-scroll-position, which adds rows above the row after it, keeps that row where
// it is.
const bottomThreshold = 32;

export function attachStickToBottom(scroller) {
    const content = scroller?.firstElementChild;
    if (!content) {
        return null;
    }

    let isPinned = false;
    let lastTop = scroller.scrollTop;
    let heldRow = null;
    let heldOffset = 0;

    const offsetOf = element => element.getBoundingClientRect().top - scroller.getBoundingClientRect().top;

    const distanceToBottom = () => scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight;

    // The chat renders only its newest rows, so a new row removes the oldest one above the view. The browser's scroll
    // anchoring answers that by scrolling up by the removed height, which moves the view off the bottom even when the
    // content height, and so no resize observation, changed. While pinned the bottom is the anchor, so the browser's
    // anchoring is switched off; on the content, because the scroller's style attribute belongs to the component.
    const setPinned = value => {
        if (isPinned !== value) {
            isPinned = value;
            content.style.overflowAnchor = value ? 'none' : '';
        }
    };

    const scrollToBottom = () => {
        scroller.scrollTop = scroller.scrollHeight;
        lastTop = scroller.scrollTop;
    };

    // Only the reader releases the pin. Layout also scrolls the timeline up: the input resizing itself briefly makes
    // the timeline taller, which clamps its scroll position, and scroll anchoring reacts to removed rows. So an
    // upward scroll releases the pin only right after wheel, touch, keyboard or scrollbar input, and then any upward
    // scroll that leaves the bottom does, however small; the threshold only applies to coming back. Scroll events
    // can arrive after content grew, which is why the direction, not the distance alone, decides.
    let userScrollUntil = 0;
    let isPointerDown = false;
    const noteUserScroll = () => {
        userScrollUntil = performance.now() + 500;
    };
    const isUserScroll = () => isPointerDown || performance.now() <= userScrollUntil;
    const onPointerDown = () => {
        isPointerDown = true;
    };
    const onPointerUp = () => {
        if (isPointerDown) {
            isPointerDown = false;
            noteUserScroll();
        }
    };
    const scrollKeys = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' ']);
    // Keys typed into the chat input, such as a space, do not scroll the timeline.
    const onKeyDown = event => {
        if (scrollKeys.has(event.key) && !event.target.closest?.('input, textarea, select, [contenteditable="true"]')) {
            noteUserScroll();
        }
    };

    const onScroll = () => {
        const top = scroller.scrollTop;
        if (top !== lastTop) {
            // The reader scrolled, or the browser's own scroll anchoring already kept the held row in place.
            heldRow = null;
        }

        if (top < lastTop) {
            if (isUserScroll()) {
                setPinned(distanceToBottom() <= 1);
            } else if (isPinned) {
                // A layout clamp moved the pinned view up and may leave no size change to observe.
                scrollToBottom();
                return;
            }
        } else if (!isPinned) {
            setPinned(distanceToBottom() <= bottomThreshold);
        }

        lastTop = top;
    };

    // Observations are delivered once per frame after layout and before paint, so streamed text scrolls at most once
    // per frame and without showing the unscrolled state first. The content grows with entries and streamed text; the
    // scroller shrinks when the input grows or an on-screen keyboard opens.
    // Browsers without scroll anchoring leave the view where it was when rows are added above, so the held row is
    // moved back once it has moved.
    const resizeObserver = new ResizeObserver(() => {
        if (heldRow) {
            const moved = heldRow.isConnected ? offsetOf(heldRow) - heldOffset : 0;
            if (!heldRow.isConnected || Math.abs(moved) >= 1) {
                heldRow = null;
                scroller.scrollTop += moved;
                lastTop = scroller.scrollTop;
            }
        }

        if (isPinned) {
            scrollToBottom();
        }
    });

    const onClick = event => {
        const row = event.target.closest?.('[data-keeps-scroll-position]')?.nextElementSibling;
        if (row) {
            heldRow = row;
            heldOffset = offsetOf(row);
        }
    };
    resizeObserver.observe(content);
    resizeObserver.observe(scroller);
    scroller.addEventListener('scroll', onScroll, { passive: true });
    scroller.addEventListener('click', onClick, true);
    scroller.addEventListener('wheel', noteUserScroll, { passive: true });
    scroller.addEventListener('touchmove', noteUserScroll, { passive: true });
    scroller.addEventListener('pointerdown', onPointerDown, { passive: true });
    window.addEventListener('pointerup', onPointerUp, { passive: true });
    window.addEventListener('pointercancel', onPointerUp, { passive: true });
    window.addEventListener('keydown', onKeyDown, { passive: true });
    setPinned(true);
    scrollToBottom();

    // The timeline is hidden until it starts at the bottom, so it does not show its top first.
    scroller.style.visibility = '';

    return {
        pin: () => {
            // Sending outranks a scroll the reader just made, so its input does not release the pin again.
            userScrollUntil = 0;
            setPinned(true);
            scrollToBottom();
        },
        dispose: () => {
            resizeObserver.disconnect();
            scroller.removeEventListener('scroll', onScroll);
            scroller.removeEventListener('click', onClick, true);
            scroller.removeEventListener('wheel', noteUserScroll);
            scroller.removeEventListener('touchmove', noteUserScroll);
            scroller.removeEventListener('pointerdown', onPointerDown);
            window.removeEventListener('pointerup', onPointerUp);
            window.removeEventListener('pointercancel', onPointerUp);
            window.removeEventListener('keydown', onKeyDown);
            content.style.overflowAnchor = '';
        }
    };
}
