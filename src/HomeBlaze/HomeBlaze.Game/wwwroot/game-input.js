// Direction indexes match GameDirection: north, north east, east, south east, south, south west, west, north west.
export const DIRECTIONS = [[0, 1], [1, 1], [1, 0], [1, -1], [0, -1], [-1, -1], [-1, 0], [-1, 1]];

const KEYS = {
    ArrowUp: 'north', KeyW: 'north',
    ArrowDown: 'south', KeyS: 'south',
    ArrowLeft: 'west', KeyA: 'west',
    ArrowRight: 'east', KeyD: 'east',
};

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

export function createInput(dotNet, options, entities) {
    const pressed = new Set();
    let isShiftHeld = false;
    let timer = null;
    let sequence = 0;
    let isDisposed = false;

    function heldDirection() {
        const offsetX = (pressed.has('east') ? 1 : 0) - (pressed.has('west') ? 1 : 0);
        const offsetY = (pressed.has('north') ? 1 : 0) - (pressed.has('south') ? 1 : 0);
        return DIRECTIONS.findIndex(([x, y]) => x === offsetX && y === offsetY && (x !== 0 || y !== 0));
    }

    function updateTarget() {
        const self = entities.getSelf();
        entities.setTarget(self ? getTargetTiles(self.x, self.y, self.width, self.depth, self.facing) : []);
    }

    async function send(direction, turnOnly) {
        const stepSequence = ++sequence;
        try {
            const result = await dotNet.invokeMethodAsync('Step', stepSequence, direction, turnOnly);
            if (result && !isDisposed && stepSequence === sequence) {
                entities.setSelf(result.x, result.y, result.facing, true);
                updateTarget();
            }
        } catch {
            // The circuit is gone; the page reconnects or reloads on its own.
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

        if (isInputBlocked()) {
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
        onServerSelf(view) {
            entities.setSelf(view.x, view.y, view.facing, true);
            updateTarget();
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
