// The text dialog, MarkdownTextDialog, whose MudDialog reports no Escape to its OnKeyDown.

/**
 * Calls CancelByEscapeAsync of a dialog for an Escape anywhere in it, also on its title, which the editor did not take, such
 * as one that closes the suggestions of the editor. Only Escape reaches the server, and not every key.
 * @param {HTMLElement} element An element inside the dialog.
 * @param {object} dialog The reference to the .NET object of the dialog.
 * @returns {{ dispose: () => void }} Stops calling the dialog.
 */
export function watchEscape(element, dialog) {
    const root = element?.closest('.mud-dialog');
    if (!root) {
        return { dispose: () => { } };
    }

    const listener = event => {
        if (event.key === 'Escape' && !event.defaultPrevented) {
            dialog.invokeMethodAsync('CancelByEscapeAsync');
        }
    };
    root.addEventListener('keydown', listener);
    return { dispose: () => root.removeEventListener('keydown', listener) };
}
