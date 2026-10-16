// Decorations and folding ranges of the page editor. The component sends records of six numbers that
// BlockOutlineDecorations.Create writes (Models/BlockOutlineDecorations.cs): kind, color, start line, start column,
// end line, end column. The kinds below must stay equal to the constants there.
const SubjectBlock = 0;
const Expression = 1;
const Head = 2;
const Fence = 3;
const ClosingFence = 4;
const Fold = 5;
const RecordLength = 6;

const collections = new WeakMap();
const foldsByModel = new Map();
let changeEmitter = null;

/**
 * Gets the text of the editor together with the version of its model.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 */
export function getContent(editorId) {
    const model = window.blazorMonaco.editor.getEditor(editorId, true)?.getModel();
    return model && !model.isDisposed() ? { version: model.getVersionId(), text: model.getValue() } : null;
}

/**
 * Replaces the decorations and the folding ranges of an editor.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 * @param {number} version The version of the model that the records were computed from, or 0 to apply them anyway.
 * @param {number[]} values The records.
 * @param {string[]} labels The labels of the closing fences, in their order.
 */
export function update(editorId, version, values, labels) {
    const editor = window.blazorMonaco.editor.getEditor(editorId, true);
    const model = editor?.getModel();
    if (!model || model.isDisposed()) {
        return;
    }

    // The records are of older text, and the change that made it newer refreshes again. Monaco has moved the
    // current decorations and folds with the edits in the meantime.
    if (version !== 0 && model.getVersionId() !== version) {
        return;
    }

    const decorations = [];
    const folds = [];
    let labelIndex = 0;
    for (let i = 0; i + RecordLength <= values.length; i += RecordLength) {
        const kind = values[i];
        const color = values[i + 1];
        const range = new monaco.Range(values[i + 2], values[i + 3], values[i + 4], values[i + 5]);
        switch (kind) {
            case SubjectBlock:
                decorations.push({ range, options: { description: "subject-block", className: "subject-block-decoration" } });
                break;
            case Expression:
                decorations.push({ range, options: { description: "expression", className: "expression-decoration" } });
                break;
            case Head:
                decorations.push({ range, options: { description: "block-head", className: `block-head block-color-${color}` } });
                break;
            case Fence:
                decorations.push({ range, options: { description: "block-fence", inlineClassName: `block-fence block-color-${color}` } });
                break;
            case ClosingFence:
                decorations.push({
                    range,
                    options: {
                        description: "block-fence",
                        inlineClassName: `block-fence block-color-${color}`,
                        after: { content: "  " + labels[labelIndex++], inlineClassName: `block-closing-label block-color-${color}` }
                    }
                });
                break;
            case Fold:
                folds.push(range);
                break;
        }
    }

    let collection = collections.get(editor);
    if (!collection) {
        collection = editor.createDecorationsCollection();
        collections.set(editor, collection);
    }

    collection.set(decorations);
    setFolds(model, folds);
}

// The folds are tracked decorations, so that Monaco moves them with every edit. The folding controller asks the
// provider again shortly after an edit, which can be before the component has sent the ranges of the new text,
// and fixed line numbers would then cut through a collapsed block and expand it.
function setFolds(model, ranges) {
    const uri = model.uri.toString();
    let folds = foldsByModel.get(uri);
    if (folds === undefined) {
        folds = { ids: [] };
        foldsByModel.set(uri, folds);
        model.onWillDispose(() => foldsByModel.delete(uri));
    }

    if (hasSameFolds(model, folds.ids, ranges)) {
        return;
    }

    folds.ids = model.deltaDecorations(folds.ids, ranges.map(range => ({
        range,
        options: { description: "block-fold", stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges }
    })));

    ensureProvider();
    changeEmitter.fire(undefined);
}

function hasSameFolds(model, ids, ranges) {
    if (ids.length !== ranges.length) {
        return false;
    }

    for (let i = 0; i < ids.length; i++) {
        const current = model.getDecorationRange(ids[i]);
        if (!current || current.startLineNumber !== ranges[i].startLineNumber || current.endLineNumber !== ranges[i].endLineNumber) {
            return false;
        }
    }

    return true;
}

// Registered once for every markdown editor. A model without folds gets undefined and not an empty list, because
// only undefined lets Monaco fall back to folding by indentation.
function ensureProvider() {
    if (changeEmitter !== null) {
        return;
    }

    changeEmitter = new monaco.Emitter();
    monaco.languages.registerFoldingRangeProvider("markdown", {
        onDidChange: changeEmitter.event,
        provideFoldingRanges(model) {
            const folds = foldsByModel.get(model.uri.toString());
            if (!folds || folds.ids.length === 0) {
                return undefined;
            }

            const ranges = [];
            for (const id of folds.ids) {
                const range = model.getDecorationRange(id);
                if (range && range.endLineNumber > range.startLineNumber) {
                    ranges.push({ start: range.startLineNumber, end: range.endLineNumber });
                }
            }

            return ranges.length > 0 ? ranges : undefined;
        }
    });
}
