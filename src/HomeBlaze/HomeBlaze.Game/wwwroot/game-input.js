// Direction indexes match GameDirection: north, north east, east, south east, south, south west, west, north west.
export const DIRECTIONS = [[0, 1], [1, 1], [1, 0], [1, -1], [0, -1], [-1, -1], [-1, 0], [-1, 1]];

const KEYS = {
    ArrowUp: 'north', KeyW: 'north',
    ArrowDown: 'south', KeyS: 'south',
    ArrowLeft: 'west', KeyA: 'west',
    ArrowRight: 'east', KeyD: 'east',
};

const PAD_BUTTONS = '[data-game-move], [data-game-turn]';
const PAD_HELD_CLASS = 'game-pad-held';
const PAD_RELEASE_EVENTS = ['pointerup', 'pointercancel', 'lostpointercapture', 'pointerleave'];

const INT_MIN = -2147483648;
const INT_MAX = 2147483647;

export function getTargetTiles(x, y, width, depth, facing) {
    const [offsetX, offsetY] = DIRECTIONS[facing];
    const tiles = [];
    for (let tileY = y + offsetY; tileY < y + offsetY + depth; tileY++) {
        for (let tileX = x + offsetX; tileX < x + offsetX + width; tileX++) {
            const isOwn = tileX >= x && tileX < x + width && tileY >= y && tileY < y + depth;
            if (!isOwn) {
                tiles.push({ x: tileX, y: tileY });
            }
        }
    }

    return tiles;
}

export function isInputBlocked() {
    const element = document.activeElement;
    if (element && (element.tagName === 'INPUT' || element.tagName === 'TEXTAREA' || element.tagName === 'SELECT' || element.isContentEditable)) {
        return true;
    }

    return document.querySelector('.mud-dialog-container') !== null;
}

// The index in DIRECTIONS of the held names such as 'north', or -1 when they cancel out or none is held.
function toDirection(isHeld) {
    const offsetX = (isHeld('east') ? 1 : 0) - (isHeld('west') ? 1 : 0);
    const offsetY = (isHeld('north') ? 1 : 0) - (isHeld('south') ? 1 : 0);
    return DIRECTIONS.findIndex(([x, y]) => x === offsetX && y === offsetY && (x !== 0 || y !== 0));
}

function intersects(x, y, width, depth, other) {
    return x < other.x + other.width && other.x < x + width && y < other.y + other.depth && other.y < y + depth;
}

function isFree(x, y, width, depth, blockers) {
    if (x < INT_MIN || y < INT_MIN || x + width - 1 > INT_MAX || y + depth - 1 > INT_MAX) {
        return false;
    }

    return !blockers.some(blocker => intersects(x, y, width, depth, blocker));
}

// Mirrors Game.TryGetStep, so a prediction only differs from the server when the world changed in between.
export function predictStep(state, direction, blockers) {
    const [offsetX, offsetY] = DIRECTIONS[direction];
    const { x, y, width, depth } = state;
    if (!isFree(x + offsetX, y + offsetY, width, depth, blockers)) {
        return { ...state, facing: direction };
    }

    if (offsetX !== 0 && offsetY !== 0) {
        const isHorizontalBlocked = !isFree(x + offsetX, y, width, depth, blockers);
        const isVerticalBlocked = !isFree(x, y + offsetY, width, depth, blockers);
        if (isHorizontalBlocked && isVerticalBlocked) {
            return { ...state, facing: direction };
        }
    }

    return { ...state, x: x + offsetX, y: y + offsetY, facing: direction };
}

/** Handles the keyboard and, when options.pad is an element, the direction pad buttons inside it. */
export function createInput(dotNet, options, entities) {
    const pressed = new Set();
    // The pad buttons held, by pointer id, so several fingers combine like several keys.
    const padPresses = new Map();
    const pad = options.pad instanceof Element ? options.pad : null;
    let isTurnMode = false;
    const pending = [];
    let isShiftHeld = false;
    let timer = null;
    let sequence = 0;
    let isDisposed = false;
    let predicted = null;
    // Counts the resets, so the answer to a step sent before one is dropped.
    let generation = 0;

    function isHeld(name) {
        if (pressed.has(name)) {
            return true;
        }

        for (const press of padPresses.values()) {
            if (press.names.includes(name)) {
                return true;
            }
        }

        return false;
    }

    function heldDirection() {
        return toDirection(isHeld);
    }

    function show(state, animate) {
        entities.setSelf(state.x, state.y, state.facing, animate);
        entities.setTarget(getTargetTiles(state.x, state.y, state.width, state.depth, state.facing));
    }

    function apply(state, step) {
        return step.turnOnly ? { ...state, facing: step.direction } : predictStep(state, step.direction, entities.getBlockers());
    }

    // Starts again from the server's verdict and replays the steps it has not answered yet.
    function reconcile(serverState) {
        let state = serverState;
        for (const step of pending) {
            state = apply(state, step);
        }

        const isDifferent = !predicted || state.x !== predicted.x || state.y !== predicted.y || state.facing !== predicted.facing;
        predicted = state;
        if (isDifferent) {
            show(predicted, true);
        }
    }

    async function send(direction, turnOnly) {
        if (!predicted) {
            return;
        }

        const step = { sequence: ++sequence, direction, turnOnly };
        const sentGeneration = generation;
        pending.push(step);
        predicted = apply(predicted, step);
        show(predicted, true);
        try {
            const result = await dotNet.invokeMethodAsync('Step', step.sequence, direction, turnOnly);
            if (isDisposed || sentGeneration !== generation) {
                return;
            }

            while (pending.length > 0 && pending[0].sequence <= step.sequence) {
                pending.shift();
            }

            // Null: the page had no player for the step, so the next server position decides.
            if (result) {
                reconcile({ ...predicted, x: result.x, y: result.y, facing: result.facing });
            }
        } catch {
            // The circuit is gone; the page reconnects or reloads on its own.
            if (sentGeneration === generation) {
                pending.length = 0;
            }
        }
    }

    function stop() {
        clearTimeout(timer);
        timer = null;
    }

    function tick() {
        const direction = heldDirection();
        if (direction < 0 || isInputBlocked()) {
            stop();
            return;
        }

        if (!isShiftHeld) {
            send(direction, false);
        }

        timer = setTimeout(tick, options.stepIntervalMs);
    }

    function onKeyDown(event) {
        if (event.key === 'Shift') {
            isShiftHeld = true;
        }

        // Leaves shortcuts such as Ctrl+R or Alt+Left to the browser.
        if (event.ctrlKey || event.altKey || event.metaKey || isInputBlocked()) {
            return;
        }

        const key = KEYS[event.code];
        if (key) {
            event.preventDefault();
            if (event.repeat) {
                return;
            }

            pressed.add(key);
            if (event.shiftKey) {
                const direction = heldDirection();
                if (direction >= 0) {
                    send(direction, true);
                }

                return;
            }

            if (timer === null) {
                tick();
            }

            return;
        }

        if (event.code.startsWith('Digit') && !event.repeat) {
            const number = Number(event.code.slice(5));
            if (number >= 1 && number <= 9) {
                event.preventDefault();
                dotNet.invokeMethodAsync('InvokeTargetAction', number - 1).catch(() => { });
            }
        }
    }

    function onKeyUp(event) {
        if (event.key === 'Shift') {
            isShiftHeld = false;
        }

        const key = KEYS[event.code];
        if (key) {
            pressed.delete(key);
            if (heldDirection() < 0) {
                stop();
            }
        }
    }

    function onPadPointerDown(event) {
        const button = event.target.closest(PAD_BUTTONS);
        if (!button || !pad.contains(button) || event.button !== 0) {
            return;
        }

        event.preventDefault();
        if (button.hasAttribute('data-game-turn')) {
            isTurnMode = !isTurnMode;
            button.setAttribute('aria-pressed', String(isTurnMode));
            return;
        }

        if (isInputBlocked()) {
            return;
        }

        const names = button.dataset.gameMove.split('-');
        if (isTurnMode) {
            send(toDirection(name => names.includes(name)), true);
            return;
        }

        releasePad(event.pointerId);
        // Keeps the press while the finger drifts off the button, until it lifts.
        try {
            button.setPointerCapture(event.pointerId);
        } catch {
            // The pointer is already gone, which the release events report.
        }

        padPresses.set(event.pointerId, { button, names });
        button.classList.add(PAD_HELD_CLASS);
        if (timer === null) {
            tick();
        }
    }

    function releasePad(pointerId) {
        const press = padPresses.get(pointerId);
        if (!press) {
            return;
        }

        padPresses.delete(pointerId);
        press.button.classList.remove(PAD_HELD_CLASS);
        if (heldDirection() < 0) {
            stop();
        }
    }

    function onPadRelease(event) {
        releasePad(event.pointerId);
    }

    function onPadContextMenu(event) {
        event.preventDefault();
    }

    function releaseAllPad() {
        for (const pointerId of [...padPresses.keys()]) {
            releasePad(pointerId);
        }
    }

    function onBlur() {
        pressed.clear();
        releaseAllPad();
        isShiftHeld = false;
        stop();
    }

    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    window.addEventListener('blur', onBlur);
    if (pad) {
        pad.querySelector('[data-game-turn]')?.setAttribute('aria-pressed', 'false');
        pad.addEventListener('pointerdown', onPadPointerDown);
        pad.addEventListener('contextmenu', onPadContextMenu);
        for (const type of PAD_RELEASE_EVENTS) {
            pad.addEventListener(type, onPadRelease);
        }
    }

    return {
        // The server's position wins only while no step is on the way; otherwise the step results reconcile.
        onServerSelf(view) {
            const serverState = { x: view.x, y: view.y, facing: view.facing, width: view.width, depth: view.depth };
            if (!predicted) {
                predicted = serverState;
                show(predicted, false);
                return;
            }

            if (pending.length === 0) {
                reconcile(serverState);
            }
        },

        /** Forgets the own player, so the next one starts from the server's position. */
        reset() {
            generation++;
            predicted = null;
            pending.length = 0;
        },

        dispose() {
            isDisposed = true;
            stop();
            window.removeEventListener('keydown', onKeyDown);
            window.removeEventListener('keyup', onKeyUp);
            window.removeEventListener('blur', onBlur);
            if (pad) {
                releaseAllPad();
                pad.removeEventListener('pointerdown', onPadPointerDown);
                pad.removeEventListener('contextmenu', onPadContextMenu);
                for (const type of PAD_RELEASE_EVENTS) {
                    pad.removeEventListener(type, onPadRelease);
                }
            }
        },
    };
}
