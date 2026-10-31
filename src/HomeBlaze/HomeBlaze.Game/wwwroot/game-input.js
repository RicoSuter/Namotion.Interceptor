// Direction indexes match GameDirection: north, north east, east, south east, south, south west, west, north west.
export const DIRECTIONS = [[0, 1], [1, 1], [1, 0], [1, -1], [0, -1], [-1, -1], [-1, 0], [-1, 1]];

const KEYS = {
    ArrowUp: 'north', KeyW: 'north',
    ArrowDown: 'south', KeyS: 'south',
    ArrowLeft: 'west', KeyA: 'west',
    ArrowRight: 'east', KeyD: 'east',
};

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

export function createInput(dotNet, options, entities) {
    const pressed = new Set();
    const pending = [];
    let isShiftHeld = false;
    let timer = null;
    let sequence = 0;
    let isDisposed = false;
    let predicted = null;

    function heldDirection() {
        const offsetX = (pressed.has('east') ? 1 : 0) - (pressed.has('west') ? 1 : 0);
        const offsetY = (pressed.has('north') ? 1 : 0) - (pressed.has('south') ? 1 : 0);
        return DIRECTIONS.findIndex(([x, y]) => x === offsetX && y === offsetY && (x !== 0 || y !== 0));
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
        pending.push(step);
        predicted = apply(predicted, step);
        show(predicted, true);
        try {
            const result = await dotNet.invokeMethodAsync('Step', step.sequence, direction, turnOnly);
            if (isDisposed) {
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
            pending.length = 0;
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

    function onBlur() {
        pressed.clear();
        isShiftHeld = false;
        stop();
    }

    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    window.addEventListener('blur', onBlur);

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

        dispose() {
            isDisposed = true;
            stop();
            window.removeEventListener('keydown', onKeyDown);
            window.removeEventListener('keyup', onKeyUp);
            window.removeEventListener('blur', onBlur);
        },
    };
}
