const observeName = 'mudObserveElementSize';

// The last timestamp given to a size of each observed element.
const lastTimestamps = new WeakMap();

// MudChart takes a size of its element only when its timestamp is later than that of the last size it took, and
// MudBlazor stamps the sizes in whole milliseconds. Of two sizes within one millisecond, like those of a layout that
// settles in two steps, the second was dropped and the chart stayed drawn at the first. The observer of MudBlazor is
// wrapped so the sizes of each element get strictly increasing timestamps, in the order they are reported.
export function beforeWebStart() {
    const observe = window[observeName];
    if (typeof observe === 'function') {
        window[observeName] = withIncreasingTimestamps(observe);
        return;
    }

    // The script of MudBlazor may run after this module, so its observer is wrapped when it is set.
    let wrapped;
    Object.defineProperty(window, observeName, {
        configurable: true,
        get: () => wrapped,
        set: value => {
            wrapped = typeof value === 'function' ? withIncreasingTimestamps(value) : value;
        }
    });
}

function withIncreasingTimestamps(observe) {
    return (reference, element, ...options) => observe({
        invokeMethodAsync: (method, size, ...args) => {
            if (element && typeof size?.timestamp === 'number') {
                const timestamp = Math.max(size.timestamp, (lastTimestamps.get(element) ?? 0) + 1);
                lastTimestamps.set(element, timestamp);
                size = { ...size, timestamp };
            }

            return reference.invokeMethodAsync(method, size, ...args);
        }
    }, element, ...options);
}
