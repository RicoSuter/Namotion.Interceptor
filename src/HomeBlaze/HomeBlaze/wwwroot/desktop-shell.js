// Sends the desktop shell back to its settings screen. The shell cannot observe key presses itself,
// because Photino exposes no accelerator API, so the page has to report them.
// Inert everywhere else: window.external exists only inside the desktop shell's web view.
(() => {
    if (typeof window.external?.sendMessage !== 'function') {
        return;
    }

    window.addEventListener('keydown', event => {
        if (event.key === ',' && (event.ctrlKey || event.metaKey)) {
            event.preventDefault();
            window.external.sendMessage('shell:settings');
        }
    });
})();
