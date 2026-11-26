// Space activates a focused state or edge, and its default action would scroll the page or the dialog. Tab and Enter keep their defaults.
export function preventSpaceScroll(container) {
    const onKeyDown = event => {
        if (event.key === ' ' && event.target instanceof Element && event.target.closest('[data-automation-clickable="true"]')) {
            event.preventDefault();
        }
    };

    container.addEventListener('keydown', onKeyDown);
    return { dispose: () => container.removeEventListener('keydown', onKeyDown) };
}
