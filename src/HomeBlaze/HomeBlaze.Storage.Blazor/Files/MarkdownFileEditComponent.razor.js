// Folding ranges of the blocks of a page, keyed by the model they were sent for. The provider is registered
// once for every markdown editor. A model without ranges gets undefined and not an empty list, because only
// undefined lets Monaco fall back to folding by indentation.
const rangesByModel = new Map();
let changeEmitter = null;

function ensureProvider() {
    if (changeEmitter !== null) {
        return;
    }

    changeEmitter = new monaco.Emitter();
    monaco.languages.registerFoldingRangeProvider("markdown", {
        onDidChange: changeEmitter.event,
        provideFoldingRanges(model) {
            return rangesByModel.get(model.uri.toString());
        }
    });
}

/**
 * Sets the folding ranges of the model that an editor shows now.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 * @param {number[]} lines Pairs of 1-based start and end lines.
 */
export function setRanges(editorId, lines) {
    const model = window.blazorMonaco.editor.getEditor(editorId, true)?.getModel();
    if (!model || model.isDisposed()) {
        return;
    }

    ensureProvider();

    const uri = model.uri.toString();
    if (!rangesByModel.has(uri)) {
        model.onWillDispose(() => rangesByModel.delete(uri));
    }

    if (lines.length === 0) {
        rangesByModel.set(uri, undefined);
    } else {
        const ranges = [];
        for (let i = 0; i + 1 < lines.length; i += 2) {
            ranges.push({ start: lines[i], end: lines[i + 1] });
        }

        rangesByModel.set(uri, ranges);
    }

    changeEmitter.fire(undefined);
}
