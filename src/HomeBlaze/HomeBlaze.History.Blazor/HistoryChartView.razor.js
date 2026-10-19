const leftProperty = '--history-chart-plot-left';
const rightProperty = '--history-chart-plot-right';
const readyAttribute = 'data-plot-ready';

// Follows the plot that MudChart draws in the lines. It marks the chart view as ready once MudChart has placed its
// value labels: it draws them left of the chart first, measures them and draws again with room for them, and the view
// hides the chart and its bands until then, so they appear where they stay. With alignBands it also keeps the
// distances of the plot area from the edges of the chart view in two CSS properties of the view, which the bands use
// as their margins. MudChart draws again whenever its size or its labels change, so every redraw is followed. Returns
// a handle that stops it.
export function followPlot(lines, alignBands) {
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

        if (!view.hasAttribute(readyAttribute) && isPlaced(lines)) {
            view.setAttribute(readyAttribute, '');
        }

        if (!alignBands) {
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

        // A view in a scaled canvas is drawn larger or smaller than its layout, and the margins are layout pixels.
        const scale = view.offsetWidth / box.width;
        setProperty(view, leftProperty, (gridLine.left - box.left) * scale);
        setProperty(view, rightProperty, (box.right - gridLine.right) * scale);
    };

    // Blazor replaces the style attribute of the view when its style changes, which drops the properties.
    const changes = new MutationObserver(update);
    changes.observe(lines, { subtree: true, childList: true, attributes: true, attributeFilter: ['d', 'viewBox', 'x'] });
    changes.observe(view, { attributes: true, attributeFilter: ['style'] });
    const resizes = new ResizeObserver(update);
    resizes.observe(lines);
    update();

    return {
        dispose: () => {
            changes.disconnect();
            resizes.disconnect();
            view.removeAttribute(readyAttribute);
            view.style.removeProperty(leftProperty);
            view.style.removeProperty(rightProperty);
        }
    };
}

// Whether the chart is drawn at the size of its box with every value label inside it. The first svg is the chart. The
// viewBox is in layout pixels, which a scaled canvas does not change, unlike the drawn size.
function isPlaced(lines) {
    const chart = lines.querySelector('svg');
    const viewBox = chart?.viewBox.baseVal;
    if (!viewBox || !chart.querySelector('.mud-charts-timeseries-series path')) {
        return false;
    }

    const size = chart.getBoundingClientRect();
    return chart.clientWidth > 0 &&
        Math.abs(viewBox.width - chart.clientWidth) <= 1 &&
        Math.abs(viewBox.height - chart.clientHeight) <= 1 &&
        [...chart.querySelectorAll('.mud-charts-yaxis text')]
            .every(text => text.textContent.trim() === '' || text.getBoundingClientRect().left >= size.left - 1);
}

// Sets only a changed value, because setting one changes the style attribute that is observed.
function setProperty(element, name, pixels) {
    const value = `${Math.max(0, Math.round(pixels * 10) / 10)}px`;
    if (element.style.getPropertyValue(name) !== value) {
        element.style.setProperty(name, value);
    }
}
