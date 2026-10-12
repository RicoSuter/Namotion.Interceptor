const missingClass = 'markdown-picture-missing';
const maximumTextLength = 34;

let isWatching = false;

// Shows a placeholder for every picture of a page that cannot be loaded, now and for as long as the tab is open.
export function watchPictures() {
    if (!isWatching) {
        isWatching = true;

        // The error event of a picture does not bubble, so it is caught on its way down.
        document.addEventListener('error', event => showPlaceholder(event.target), true);
    }

    // A picture that failed before the listener was there is loaded once more, so that it reports its error again.
    for (const picture of document.querySelectorAll(`.markdown-content img[src]:not(.${missingClass})`)) {
        if (picture.complete && picture.naturalWidth === 0) {
            picture.src = picture.getAttribute('src');
        }
    }
}

// Only the attributes of the picture change: the nodes of a page belong to Blazor, which would lose track of a node
// that is added or removed here.
function showPlaceholder(picture) {
    if (!(picture instanceof HTMLImageElement) || !picture.closest('.markdown-content') || picture.classList.contains(missingClass)) {
        return;
    }

    picture.classList.add(missingClass);
    if (!picture.title) {
        picture.title = picture.currentSrc || picture.src;
    }

    picture.removeAttribute('srcset');
    picture.src = createPlaceholder(picture.alt);
}

// The colour is a grey that can be read on a light and on a dark page, because a picture cannot use the colours of the page.
function createPlaceholder(text) {
    const shortText = text.length > maximumTextLength ? `${text.substring(0, maximumTextLength - 1)}…` : text;
    const textLine = shortText
        ? `<text x="72" y="64" font-size="13" fill-opacity="0.8">${escapeXml(shortText)}</text>`
        : '';
    const svg =
        '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="96" viewBox="0 0 320 96" font-family="sans-serif" fill="#888">' +
        '<rect x="1" y="1" width="318" height="94" rx="6" fill="none" stroke="#888" stroke-opacity="0.6" stroke-dasharray="6 4"/>' +
        '<path transform="translate(22 30) scale(1.5)" d="M21 5v6.59l-3-3.01-4 4.01-4-4-4 4-3-3.01V5c0-1.1.9-2 2-2h14c1.1 0 2 .9 2 2zm-3 6.42l3 3.01V19c0 1.1-.9 2-2 2H5c-1.1 0-2-.9-2-2v-6.58l3 2.99 4-4 4 4 4-3.99z"/>' +
        `<text x="72" y="${shortText ? 43 : 53}" font-size="15">Picture not available</text>` +
        textLine +
        '</svg>';
    return `data:image/svg+xml,${encodeURIComponent(svg)}`;
}

function escapeXml(text) {
    return text.replace(/[&<>"']/g, character => `&#${character.charCodeAt(0)};`);
}
