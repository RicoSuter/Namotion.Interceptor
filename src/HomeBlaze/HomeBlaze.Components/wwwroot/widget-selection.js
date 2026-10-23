// Keyboard support of the selectable widgets in edit mode. MudBlazor renders the action bar of the selected widget at the end
// of the page, where Tab would reach it last. The handlers below put it into the focus order right after the widget, and keep
// the focus on a widget while the bar goes away.
(() => {
    const tabbableSelector =
        'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), ' +
        'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

    const getTabbables = root => Array.prototype.filter.call(
        root.querySelectorAll(tabbableSelector),
        element => element.getClientRects().length > 0);

    // Where Tab goes from the widget when there is no bar: the next element in document order, which can be inside the widget.
    const getNextAfter = (widget, bar) => getTabbables(document).find(element =>
        element !== widget &&
        !bar.contains(element) &&
        (widget.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0);

    const findSelection = () => {
        const bar = document.querySelector('.subject-action-bar');
        const widget = bar && document.querySelector('.subject-component-wrapper.selected');
        return widget ? { bar, widget } : null;
    };

    // Space on a focused widget acts as Enter, as on a button. The server cannot tell the widget from an input inside it, so
    // only a Space whose target is the widget itself is replaced, before it scrolls the page or reaches the server.
    document.addEventListener('keydown', event => {
        if (event.key !== ' ' || !(event.target instanceof Element) ||
            !event.target.matches('.subject-component-wrapper.selectable')) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        event.target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true }));
    }, true);

    // Capturing, so that the focus has moved before the page handles Escape and removes the bar that holds it.
    document.addEventListener('keydown', event => {
        if (event.key !== 'Tab' && event.key !== 'Escape') {
            return;
        }

        const selection = findSelection();
        if (!selection) {
            return;
        }

        const { bar, widget } = selection;
        const active = document.activeElement;
        if (event.key === 'Escape') {
            if (bar.contains(active)) {
                widget.focus({ preventScroll: true });
            }

            return;
        }

        const buttons = getTabbables(bar);
        if (buttons.length === 0) {
            return;
        }

        let target = null;
        if (!event.shiftKey) {
            if (active === widget) {
                target = buttons[0];
            } else if (active === buttons[buttons.length - 1]) {
                target = getNextAfter(widget, bar);
            }
        } else if (active === buttons[0]) {
            target = widget;
        } else if (active === getNextAfter(widget, bar)) {
            target = buttons[buttons.length - 1];
        }

        if (target) {
            event.preventDefault();
            target.focus();
        }
    }, true);

    // Capturing, so that it runs before the double click reaches the server. A control that a widget offers in edit mode, such
    // as a tab, an Add button or an insert line, keeps a double click from the widget around it, which would open its edit
    // dialog. A control that is a widget of its own has its own wrapper, closer than the control. Any other double click on a
    // block opens its dialog, and the word that the browser selected with it would stay highlighted behind the dialog.
    document.addEventListener('dblclick', event => {
        if (!(event.target instanceof Element)) {
            return;
        }

        const block = event.target.closest('.subject-component-wrapper');
        const control = event.target.closest('.edit-mode-interactive');
        if (control && block?.contains(control)) {
            event.stopPropagation();
        } else if (block?.matches('.selectable, .text-block') && !event.target.closest('input, textarea, [contenteditable]')) {
            window.getSelection()?.removeAllRanges();
        }
    }, true);

    // Called once the widget that an edit selected rendered. An edit that replaced the element with the focus, such as the
    // moved widget or the button of its bar, left the focus on the body, and a keyboard user would start over from the top.
    // The page stays where it is, so that a widget only partly in view does not jump.
    window.widgetSelection = {
        restoreFocus: () => {
            const active = document.activeElement;
            if (!active || active === document.body) {
                document.querySelector('.subject-component-wrapper.selected')?.focus({ preventScroll: true });
            }
        }
    };

    // The parent is selected next, so it takes the focus from the button, which goes away with the bar of the child.
    document.addEventListener('click', event => {
        if (!(event.target instanceof Element) || !event.target.closest('.subject-action-bar .select-parent-button')) {
            return;
        }

        const widget = document.querySelector('.subject-component-wrapper.selected');
        widget?.parentElement?.closest('.subject-component-wrapper.selectable')?.focus({ preventScroll: true });
    }, true);
})();
