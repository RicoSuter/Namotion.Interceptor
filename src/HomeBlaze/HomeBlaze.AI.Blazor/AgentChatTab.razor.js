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

// Keeps the timeline scrolled to the newest content while the reader is at the bottom. Scrolling up releases it, so
// new content never moves the view; scrolling back to the bottom or pin() holds it again.
const bottomThreshold = 32;

export function attachStickToBottom(scroller) {
    const content = scroller?.firstElementChild;
    if (!content) {
        return null;
    }

    let isPinned = true;
    let lastTop = scroller.scrollTop;

    const isAtBottom = () => scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight <= bottomThreshold;

    const scrollToBottom = () => {
        scroller.scrollTop = scroller.scrollHeight;
        lastTop = scroller.scrollTop;
    };

    // Only scrolling up releases the pin. A scroll event can be dispatched after content grew, so judging every
    // event by its distance from the bottom would release the pin after a programmatic scroll to the bottom.
    const onScroll = () => {
        const top = scroller.scrollTop;
        if (top < lastTop || !isPinned) {
            isPinned = isAtBottom();
        }

        lastTop = top;
    };

    // Observations are delivered once per frame after layout and before paint, so streamed text scrolls at most once
    // per frame and without showing the unscrolled state first. The content grows with entries and streamed text; the
    // scroller shrinks when the input grows or an on-screen keyboard opens.
    const resizeObserver = new ResizeObserver(() => {
        if (isPinned) {
            scrollToBottom();
        }
    });
    resizeObserver.observe(content);
    resizeObserver.observe(scroller);
    scroller.addEventListener('scroll', onScroll, { passive: true });
    scrollToBottom();

    return {
        pin: () => {
            isPinned = true;
            scrollToBottom();
        },
        dispose: () => {
            resizeObserver.disconnect();
            scroller.removeEventListener('scroll', onScroll);
        }
    };
}
