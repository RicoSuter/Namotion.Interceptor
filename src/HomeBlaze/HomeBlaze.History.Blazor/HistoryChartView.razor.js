const leftProperty = '--history-chart-plot-left';
const rightProperty = '--history-chart-plot-right';

// Keeps the distances of the plot area of the lines from the edges of the chart view in two CSS properties of the
// view, which the bands use as their margins. MudChart sizes the space left of the plot from its measured value
// labels and draws again whenever its size or its labels change, so every redraw is followed. Returns a handle that
// stops it.
export function alignBands(lines) {
    const view = lines?.parentElement;

    // The element is gone when the chart was removed while this module loaded.
    if (!view) {
        return { dispose: () => { } };
    }

    const update = () => {
        // A view that is not displayed, like one in a hidden tab, has no plot to measure. It keeps its margins, and the
        // resize observer updates them when it is shown again.
        const box = view.getBoundingClientRect();
        if (box.width === 0) {
            return;
        }

        // A horizontal grid line spans the plot area from its left to its right edge. A compact chart draws no grid
        // lines and its plot fills the view, so its bands need no margins.
        const gridLine = [...lines.querySelectorAll('.mud-charts-grid path')]
            .map(path => path.getBoundingClientRect())
            .find(line => line.height < 1 && line.width > 0);
        if (!gridLine) {
            view.style.removeProperty(leftProperty);
            view.style.removeProperty(rightProperty);
            return;
        }

        setProperty(view, leftProperty, gridLine.left - box.left);
        setProperty(view, rightProperty, box.right - gridLine.right);
    };

    // Blazor replaces the style attribute of the view when its style changes, which drops the properties.
    const changes = new MutationObserver(update);
    changes.observe(lines, { subtree: true, childList: true, attributes: true, attributeFilter: ['d', 'viewBox'] });
    changes.observe(view, { attributes: true, attributeFilter: ['style'] });
    const resizes = new ResizeObserver(update);
    resizes.observe(lines);
    update();

    return {
        dispose: () => {
            changes.disconnect();
            resizes.disconnect();
            view.style.removeProperty(leftProperty);
            view.style.removeProperty(rightProperty);
        }
    };
}

// Sets only a changed value, because setting one changes the style attribute that is observed.
function setProperty(element, name, pixels) {
    const value = `${Math.max(0, Math.round(pixels * 10) / 10)}px`;
    if (element.style.getPropertyValue(name) !== value) {
        element.style.setProperty(name, value);
    }
}
