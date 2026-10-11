// Sends the chat message on Enter. Shift+Enter keeps the default line break, and Enter that confirms an IME
// composition is left to the input method.
export function attachEnterToSend(container, dotNetReference) {
    const textarea = container.querySelector('textarea');
    if (!textarea) {
        return null;
    }

    const onKeyDown = event => {
        if (event.key !== 'Enter' || event.shiftKey || event.isComposing || event.keyCode === 229) {
            return;
        }

        event.preventDefault();
        dotNetReference.invokeMethodAsync('SendFromKeyboardAsync', textarea.value);
    };

    textarea.addEventListener('keydown', onKeyDown);
    return {
        dispose: () => textarea.removeEventListener('keydown', onKeyDown)
    };
}
