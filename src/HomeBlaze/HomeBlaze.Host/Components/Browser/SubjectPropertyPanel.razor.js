const registeredElements = new WeakSet();

// Copies the value of a copy button within the element in the click itself, as browsers refuse to copy once the
// click is over, which a round trip to the server would be. Reports whether the copy succeeded. An element is
// registered once, however often it is passed.
export function registerCopyButtons(element, dotNetReference) {
    if (registeredElements.has(element)) {
        return;
    }

    registeredElements.add(element);
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

const draggingRegisteredElements = new WeakSet();
const dragThreshold = 4;
const autoScrollEdge = 48;
const autoScrollMaximumSpeed = 16;

// The drag under way on the page, which stays set until its drop is written, so that a second drag cannot start from
// the order before the first one.
let activeDrag = null;

// Moves a row of a reorder list within the element by dragging it: a mouse drags the whole row, a touch or a pen only
// its handle, so that a swipe on a row still scrolls. A drop that changes the order calls OnChildDropped with the
// name of the dragged child and the name of the child whose place it takes. An element is registered once, however
// often it is passed.
export function registerChildDragging(element, dotNetReference) {
    if (draggingRegisteredElements.has(element)) {
        return;
    }

    draggingRegisteredElements.add(element);
    element.addEventListener('pointerdown', event => {
        if (activeDrag !== null || !event.isPrimary || event.button !== 0) {
            return;
        }

        const row = event.target.closest('[data-reorder-row]');
        const list = row?.closest('[data-reorder-list]');
        if (!list || !element.contains(list) || list.dataset.canDrag !== 'true' || event.target.closest('button') !== null ||
            (event.pointerType !== 'mouse' && event.target.closest('[data-reorder-handle]') === null)) {
            return;
        }

        activeDrag = new RowDrag(element, list, row, event, dotNetReference);
    });
}

class RowDrag {
    constructor(root, list, row, event, dotNetReference) {
        this.root = root;
        this.list = list;
        this.row = row;
        this.dotNetReference = dotNetReference;
        this.pointerId = event.pointerId;
        this.pointerType = event.pointerType;
        this.startX = event.clientX;
        this.startY = event.clientY;
        this.pointerX = event.clientX;
        this.pointerY = event.clientY;
        this.isStarted = false;

        this.onPointerMove = this.onPointerMove.bind(this);
        this.onPointerUp = this.onPointerUp.bind(this);
        this.onPointerCancel = this.onPointerCancel.bind(this);
        this.onKeyDown = this.onKeyDown.bind(this);
        this.onInterruption = this.onInterruption.bind(this);
        this.onContextMenu = this.onContextMenu.bind(this);
        this.onScroll = this.onScroll.bind(this);
        this.onLostPointerCapture = this.onLostPointerCapture.bind(this);
        this.onAnimationFrame = this.onAnimationFrame.bind(this);
        document.addEventListener('pointermove', this.onPointerMove);
        document.addEventListener('pointerup', this.onPointerUp);
        document.addEventListener('pointercancel', this.onPointerCancel);

        // A context menu or a switch to another window can swallow the release of the button, after which the next
        // click would drop the row.
        document.addEventListener('contextmenu', this.onContextMenu);
        window.addEventListener('blur', this.onInterruption);
    }

    onPointerMove(event) {
        if (event.pointerId !== this.pointerId) {
            return;
        }

        if (event.pointerType !== 'touch' && (event.buttons & 1) === 0) {
            this.end();
            return;
        }

        this.pointerX = event.clientX;
        this.pointerY = event.clientY;
        if (!this.isStarted) {
            if (Math.hypot(this.pointerX - this.startX, this.pointerY - this.startY) < dragThreshold) {
                return;
            }

            this.start();
        }

        this.update();
    }

    start() {
        this.isStarted = true;
        this.rows = [...this.list.querySelectorAll('[data-reorder-row]')];
        this.names = this.rows.map(row => row.dataset.childName);
        this.index = this.rows.indexOf(this.row);

        // The preview is a copy in the body, so that it follows the pointer past the edges of the pane, which clip
        // the row and would grow its scroll area. It keeps the point of the row that was grabbed under the pointer.
        const rowRectangle = this.row.getBoundingClientRect();
        this.grabX = this.startX - rowRectangle.left;
        this.grabY = this.startY - rowRectangle.top;
        this.preview = this.row.cloneNode(true);
        // Without the attributes by which tests and Blazor find an element, which would find the copy as well.
        for (const element of [this.preview, ...this.preview.querySelectorAll('*')]) {
            for (const attribute of [...element.attributes]) {
                if (attribute.name === 'id' || attribute.name.startsWith('_bl_') || attribute.name.startsWith('data-')) {
                    element.removeAttribute(attribute.name);
                }
            }
        }

        this.preview.classList.add('subject-pane-reorder-preview');
        this.preview.setAttribute('aria-hidden', 'true');
        this.preview.inert = true;
        this.preview.style.width = `${rowRectangle.width}px`;
        this.preview.style.height = `${rowRectangle.height}px`;
        document.body.appendChild(this.preview);

        this.indicator = document.createElement('div');
        this.indicator.className = 'subject-pane-reorder-indicator';
        document.body.appendChild(this.indicator);

        this.row.dataset.dragSource = '';
        this.list.dataset.dragging = '';
        this.scroller = findScroller(this.list);
        try {
            this.row.setPointerCapture(this.pointerId);
        } catch {
            // The pointer is gone already, which ends the drag with its pointercancel.
        }

        this.row.addEventListener('lostpointercapture', this.onLostPointerCapture);
        document.addEventListener('keydown', this.onKeyDown, true);

        // Scroll events do not bubble, so the listener captures the scrolling of any element, the pane among them.
        document.addEventListener('scroll', this.onScroll, true);

        // A render that changes the rows while the drag is under way ends it, as the place it would drop on is gone.
        // A render that shows the new order once the drop is written ends the drop.
        this.observer = new MutationObserver(() => this.onMutation());
        this.observer.observe(this.root, { childList: true, subtree: true });
        this.animationFrame = requestAnimationFrame(this.onAnimationFrame);
    }

    update() {
        this.preview.style.transform = `translate(${this.pointerX - this.grabX}px, ${this.pointerY - this.grabY}px)`;

        const gap = this.getGap();
        if (gap === this.index || gap === this.index + 1) {
            this.indicator.style.display = 'none';
            return;
        }

        const listRectangle = this.list.getBoundingClientRect();
        this.indicator.style.display = 'block';
        this.indicator.style.width = `${listRectangle.width}px`;
        this.indicator.style.transform = `translate(${listRectangle.left}px, ${this.getGapTop(gap) - 1}px)`;
    }

    // The place between the rows to drop on, from 0 before the first row to the count of rows after the last one:
    // before the row under the pointer while the pointer is in its upper half, after it in its lower half.
    getGap() {
        for (let index = 0; index < this.rows.length; index++) {
            if (index === this.index) {
                continue;
            }

            const rectangle = this.rows[index].getBoundingClientRect();
            if (this.pointerY < rectangle.top + rectangle.height / 2) {
                return index;
            }
        }

        return this.rows.length;
    }

    getGapTop(gap) {
        return gap < this.rows.length
            ? this.rows[gap].getBoundingClientRect().top
            : this.rows[this.rows.length - 1].getBoundingClientRect().bottom;
    }

    onAnimationFrame() {
        const { top, bottom } = this.scroller === document.scrollingElement
            ? { top: 0, bottom: window.innerHeight }
            : this.scroller.getBoundingClientRect();

        let speed = 0;
        if (this.pointerY < top + autoScrollEdge) {
            speed = -autoScrollMaximumSpeed * Math.min(1, (top + autoScrollEdge - this.pointerY) / autoScrollEdge);
        } else if (this.pointerY > bottom - autoScrollEdge) {
            speed = autoScrollMaximumSpeed * Math.min(1, (this.pointerY - bottom + autoScrollEdge) / autoScrollEdge);
        }

        if (speed !== 0) {
            this.scroller.scrollTop += speed;
        }

        this.animationFrame = requestAnimationFrame(this.onAnimationFrame);
    }

    onPointerUp(event) {
        if (event.pointerId !== this.pointerId) {
            return;
        }

        if (!this.isStarted) {
            this.end();
            return;
        }

        this.pointerX = event.clientX;
        this.pointerY = event.clientY;
        const gap = this.getGap();
        if (gap === this.index || gap === this.index + 1 || !this.list.isConnected || this.list.dataset.canDrag !== 'true') {
            this.end();
            return;
        }

        this.drop(gap);
    }

    // The preview settles where the row lands and stays until the pane shows the new order, so that the row does not
    // jump back to its old place meanwhile. The call ends after the render of the new order, or after a move that
    // failed or was ignored, which leave the order as it was.
    drop(gap) {
        this.removeGestureListeners();
        cancelAnimationFrame(this.animationFrame);
        this.indicator.style.display = 'none';

        const rowRectangle = this.row.getBoundingClientRect();
        const top = this.getGapTop(gap) - (gap > this.index ? rowRectangle.height : 0);
        this.preview.style.transition = 'transform 100ms ease-out';
        this.preview.style.transform = `translate(${rowRectangle.left}px, ${top}px)`;

        const targetIndex = gap < this.index ? gap : gap - 1;
        this.dotNetReference
            .invokeMethodAsync('OnChildDropped', this.names[this.index], this.names[targetIndex])
            .catch(() => {
                // The pane is gone.
            })
            .finally(() => this.end());
    }

    onPointerCancel(event) {
        if (event.pointerId === this.pointerId) {
            this.end();
        }
    }

    onInterruption() {
        this.end();
    }

    // A long press, the usual start of a drag by touch or by pen, opens the context menu as well, which is suppressed.
    onContextMenu(event) {
        if (this.pointerType === 'mouse') {
            this.end();
        } else {
            event.preventDefault();
        }
    }

    // The event bubbles, and the element that a touch started on loses its own capture to the row.
    onLostPointerCapture(event) {
        if (event.target === this.row) {
            this.end();
        }
    }

    onScroll() {
        this.update();
    }

    onKeyDown(event) {
        if (event.key === 'Escape') {
            event.preventDefault();
            event.stopPropagation();
            this.end();
        }
    }

    onMutation() {
        const rows = this.list.querySelectorAll('[data-reorder-row]');
        const isChanged = !this.list.isConnected ||
            !this.row.isConnected ||
            rows.length !== this.names.length ||
            [...rows].some((row, index) => row.dataset.childName !== this.names[index]);

        if (isChanged) {
            this.end();
        }
    }

    removeGestureListeners() {
        document.removeEventListener('pointermove', this.onPointerMove);
        document.removeEventListener('pointerup', this.onPointerUp);
        document.removeEventListener('pointercancel', this.onPointerCancel);
        document.removeEventListener('contextmenu', this.onContextMenu);
        window.removeEventListener('blur', this.onInterruption);
        this.row.removeEventListener('lostpointercapture', this.onLostPointerCapture);
        document.removeEventListener('keydown', this.onKeyDown, true);
        document.removeEventListener('scroll', this.onScroll, true);
    }

    end() {
        if (activeDrag !== this) {
            return;
        }

        activeDrag = null;
        this.removeGestureListeners();
        if (!this.isStarted) {
            return;
        }

        cancelAnimationFrame(this.animationFrame);
        this.observer.disconnect();
        this.preview.remove();
        this.indicator.remove();
        delete this.row.dataset.dragSource;
        delete this.list.dataset.dragging;
        if (this.row.hasPointerCapture(this.pointerId)) {
            this.row.releasePointerCapture(this.pointerId);
        }
    }
}

function findScroller(element) {
    for (let ancestor = element.parentElement; ancestor !== null; ancestor = ancestor.parentElement) {
        const overflow = getComputedStyle(ancestor).overflowY;
        if ((overflow === 'auto' || overflow === 'scroll') && ancestor.scrollHeight > ancestor.clientHeight) {
            return ancestor;
        }
    }

    return document.scrollingElement;
}
