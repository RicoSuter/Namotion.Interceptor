// Page-side half of the desktop shell, for what the shell cannot observe from outside the web view.
// Inert everywhere else: window.external exists only inside the desktop shell's web view.
(() => {
    if (typeof window.external?.sendMessage !== 'function') {
        return;
    }

    // Sends the desktop shell back to its settings screen. The shell cannot observe key presses itself,
    // because Photino exposes no accelerator API, so the page has to report them.
    window.addEventListener('keydown', event => {
        if (event.key === ',' && (event.ctrlKey || event.metaKey)) {
            event.preventDefault();
            window.external.sendMessage('shell:settings');
        }
    });

    // Hands links to other sites to the system browser instead. The window has no address bar or back
    // button, so navigating it away from HomeBlaze would strand the user there, and Photino exposes no
    // navigation hook the shell could use to catch this itself. The shell validates the URL again.
    const toExternalUrl = value => {
        let url;
        try {
            url = new URL(value, document.baseURI);
        } catch {
            return null;
        }

        if (url.protocol === 'mailto:') {
            return url.href;
        }

        return (url.protocol === 'http:' || url.protocol === 'https:') && url.origin !== window.location.origin
            ? url.href
            : null;
    };

    const openExternally = url => window.external.sendMessage('shell:open-external:' + url);

    // Capture phase on window, so a handler that stops propagation cannot let the navigation through. This
    // runs before any page handler, so a link to another site opens externally even if the page cancels it.
    const onLinkActivated = event => {
        if (event.button !== (event.type === 'click' ? 0 : 1)) {
            return;
        }

        const link = event.composedPath().find(node => node instanceof Element && node.matches('a[href], area[href]'));
        const url = link ? toExternalUrl(link.getAttribute('href')) : null;
        if (url !== null) {
            event.preventDefault();
            openExternally(url);
        }
    };

    window.addEventListener('click', onLinkActivated, true);
    window.addEventListener('auxclick', onLinkActivated, true);

    const open = window.open;
    window.open = function (url) {
        const externalUrl = url === undefined ? null : toExternalUrl(url);
        if (externalUrl === null) {
            return open.apply(window, arguments);
        }

        openExternally(externalUrl);
        return null;
    };
})();
