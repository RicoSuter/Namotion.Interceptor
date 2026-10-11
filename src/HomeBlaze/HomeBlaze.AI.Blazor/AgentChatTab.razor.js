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
