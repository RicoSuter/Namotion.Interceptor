// The number of reasons snapping is suspended for, per pane container.
const suspensions = new WeakMap();

// Snapping is off while the panes change or the row is scrolled from here. A browser snaps again when the panes
// change, and WebKit then follows the last pane in view instead of the first, which moves the row at once instead
// of leaving the move to scrollToPane; snapping that comes back during a scroll would cut it short the same way.
function suspendSnapping(container) {
    suspensions.set(container, (suspensions.get(container) ?? 0) + 1);
    container.style.setProperty('--pane-snap', 'none');
}

function resumeSnapping(container) {
    const count = (suspensions.get(container) ?? 1) - 1;
    suspensions.set(container, count);
    if (count === 0) {
        container.style.removeProperty('--pane-snap');
    }
}

// Sizes the panes so that a whole number of them fills the container, which keeps a pane from being cut off at an
// edge, and keeps doing so as the container is resized. Returns a handle that stops it.
export function fitPanes(container, minimumPaneWidth, gap) {
    // The element is gone when the page was left while this module loaded.
    if (!container) {
        return { dispose: () => { } };
    }

    const update = () => {
        const width = container.clientWidth;
        const count = Math.max(1, Math.floor((width + gap) / (minimumPaneWidth + gap)));
        container.style.setProperty('--pane-width', `${Math.floor((width - gap * (count - 1)) / count)}px`);
    };

    const resizes = new ResizeObserver(update);
    resizes.observe(container);
    update();

    // The observer runs before the changed panes are laid out, and two frames later they have been.
    const changes = new MutationObserver(() => {
        suspendSnapping(container);
        requestAnimationFrame(() => requestAnimationFrame(() => resumeSnapping(container)));
    });
    changes.observe(container, { childList: true });

    return {
        dispose: () => {
            resizes.disconnect();
            changes.disconnect();
        }
    };
}

// Scrolls the pane at the index to the end of the view, with as many of the panes before it as fit. Resolves once
// the scrolling has ended.
export function scrollToPane(container, index, smooth) {
    const panes = container ? [...container.children].map(pane => pane.getBoundingClientRect()) : [];
    if (index < 0 || index >= panes.length) {
        return Promise.resolve();
    }

    let first = index;
    while (first > 0 && panes[index].right - panes[first - 1].left <= container.clientWidth) {
        first--;
    }

    const maximum = container.scrollWidth - container.clientWidth;
    const left = Math.min(maximum, container.scrollLeft + panes[first].left - container.getBoundingClientRect().left);
    if (!smooth || Math.abs(container.scrollLeft - left) < 1) {
        container.scrollTo({ left, behavior: 'instant' });
        return Promise.resolve();
    }

    return new Promise(resolve => {
        const finish = () => {
            container.removeEventListener('scrollend', finish);
            clearTimeout(timeout);
            resumeSnapping(container);
            resolve();
        };

        // A browser without the scrollend event finishes through the timeout.
        const timeout = setTimeout(finish, 1000);
        container.addEventListener('scrollend', finish);
        suspendSnapping(container);
        container.scrollTo({ left, behavior: 'smooth' });
    });
}

// A cookie and not local storage, so that the server already renders the page with the layout. Kept for a year.
export function saveRowLayout(cookieName, value) {
    document.cookie = `${cookieName}=${encodeURIComponent(value)}; path=/; max-age=31536000; samesite=lax`;
}
