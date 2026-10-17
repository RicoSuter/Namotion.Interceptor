const registeredElements = new WeakSet();

// Registers the copy buttons within the element. An element is registered once, however often it is passed.
export function registerPane(element, dotNetReference) {
    if (registeredElements.has(element)) {
        return;
    }

    registeredElements.add(element);
    registerCopyButtons(element, dotNetReference);
}

// Copies the value of a copy button within the element in the click itself, as browsers refuse to copy once the
// click is over, which a round trip to the server would be. Reports whether the copy succeeded.
function registerCopyButtons(element, dotNetReference) {
    element.addEventListener('click', event => {
        const button = event.target.closest('[data-copy-value]');
        if (button === null || !element.contains(button)) {
            return;
        }

        copyText(button.dataset.copyValue)
            .then(isCopied => dotNetReference.invokeMethodAsync('OnValueCopied', isCopied))
            .catch(() => {
                // The pane is gone.
            });
    });
}

// navigator.clipboard exists only in a secure context, which a page served over plain HTTP on the LAN is not.
function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
        return navigator.clipboard.writeText(text).then(() => true, () => copyBySelection(text));
    }

    return Promise.resolve(copyBySelection(text));
}

function copyBySelection(text) {
    const activeElement = document.activeElement;
    const textArea = document.createElement('textarea');
    textArea.value = text;
    textArea.setAttribute('readonly', '');
    textArea.style.position = 'fixed';
    textArea.style.opacity = '0';
    document.body.appendChild(textArea);
    textArea.select();
    textArea.setSelectionRange(0, text.length);

    let isCopied;
    try {
        isCopied = document.execCommand('copy');
    } catch {
        isCopied = false;
    }

    textArea.remove();
    activeElement?.focus?.();
    return isCopied;
}
