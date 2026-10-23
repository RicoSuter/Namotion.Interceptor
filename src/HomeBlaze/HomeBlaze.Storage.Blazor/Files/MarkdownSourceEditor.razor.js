// The editor of Markdown, MarkdownSourceEditor: the decorations and folding ranges of Monaco, and the text area that stands
// in for Monaco on a device with a coarse pointer. The component sends records of six numbers that
// BlockOutlineDecorations.Create writes (Models/BlockOutlineDecorations.cs): kind, color, start line, start column, end
// line, end column. The kinds below must stay equal to the constants there.
const SubjectBlock = 0;
const Expression = 1;
const Head = 2;
const Fence = 3;
const ClosingFence = 4;
const Fold = 5;
const Name = 6;
const RecordLength = 6;

const collections = new WeakMap();
const foldsByModel = new Map();
const focusedTextAreas = new WeakSet();
let changeEmitter = null;

/** Gets whether the primary pointer of the device is coarse, such as a finger, for which Monaco does not work. */
export function isCoarsePointer() {
    return matchMedia('(pointer: coarse)').matches;
}

/**
 * Takes the editor that has the id now, so that it can be disposed later even when another editor has the id by then.
 * BlazorMonaco keeps an editor and its model until another editor with the same id is created.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 * @returns {{ dispose: () => void }} Disposes the editor together with its model and forgets it.
 */
export function own(editorId) {
    const editor = window.blazorMonaco.editor.getEditor(editorId, true);

    // BlazorMonaco disposes the editor itself when a new editor takes its id first.
    let isDisposed = false;
    editor?.onDidDispose(() => isDisposed = true);
    return {
        dispose: () => {
            if (!editor || isDisposed) {
                return;
            }

            const editors = window.blazorMonaco.editors;
            const index = editors.findIndex(holder => holder.editor === editor);
            if (index >= 0) {
                editors.splice(index, 1);
            }

            const model = editor.getModel();
            editor.dispose();
            if (model && !model.isDisposed()) {
                model.dispose();
            }
        }
    };
}

/**
 * Disposes the editor that has the id now together with its model and forgets it.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 */
export function disposeEditor(editorId) {
    own(editorId).dispose();
}

/**
 * Starts to remember whether the text area of an editor had the focus, before which it has no cursor of the user, and
 * calls RequestSaveFromTextAreaAsync of the editor for Ctrl+S, or Cmd+S on a Mac.
 * @param {HTMLTextAreaElement} element The text area.
 * @param {object | null} editor The reference to the .NET object of the editor, or null when it does not save.
 */
export function watchTextArea(element, editor) {
    if (!element) {
        return;
    }

    element.addEventListener('focus', () => focusedTextAreas.add(element), { once: true });
    if (editor) {
        element.addEventListener('keydown', event => {
            if ((event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 's') {
                event.preventDefault();
                editor.invokeMethodAsync('RequestSaveFromTextAreaAsync').catch(() => { });
            }
        });
    }
}

/**
 * Gets the text of a text area, in which every line ends with a line feed.
 * @param {HTMLTextAreaElement} element The text area.
 */
export function getTextAreaText(element) {
    return element ? element.value : null;
}

/**
 * Replaces the text of a text area.
 * @param {HTMLTextAreaElement} element The text area.
 * @param {string} text The new text.
 */
export function setTextAreaText(element, text) {
    if (element) {
        element.value = text;
    }
}

/**
 * Gets the 1-based line of the cursor of a text area, or null while it never had the focus.
 * @param {HTMLTextAreaElement} element The text area.
 */
export function getTextAreaCursorLine(element) {
    if (!element || !focusedTextAreas.has(element)) {
        return null;
    }

    return element.value.slice(0, element.selectionStart).split('\n').length;
}

/**
 * Selects lines of a text area, scrolls their start into view and gives the text area the focus.
 * @param {HTMLTextAreaElement} element The text area.
 * @param {number} startLine The 1-based first line.
 * @param {number} endLine The 1-based last line.
 */
export function revealTextArea(element, startLine, endLine) {
    if (!element) {
        return;
    }

    const lines = element.value.split('\n');
    const start = lines.slice(0, startLine - 1).reduce((offset, line) => offset + line.length + 1, 0);
    const end = Math.min(element.value.length, lines.slice(0, endLine).reduce((offset, line) => offset + line.length + 1, 0) - 1);
    element.focus();

    // Backward, so that the browser scrolls to the start of the lines, where the cursor then is.
    element.setSelectionRange(start, Math.max(start, end), 'backward');
}

/**
 * Places the cursor at the end of the text of a text area, scrolls it into view and gives the text area the focus.
 * @param {HTMLTextAreaElement} element The text area.
 */
export function focusTextAreaEnd(element) {
    if (!element) {
        return;
    }

    element.focus();
    element.setSelectionRange(element.value.length, element.value.length);
    element.scrollTop = element.scrollHeight;
}

/**
 * Gets the text of the editor together with the version of its model.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 */
export function getContent(editorId) {
    const model = window.blazorMonaco.editor.getEditor(editorId, true)?.getModel();
    return model && !model.isDisposed() ? { version: model.getVersionId(), text: model.getValue() } : null;
}

/**
 * Selects the lines of a block, scrolls them into the middle of the editor and gives the editor the focus.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 * @param {number} startLine The 1-based head line of the block.
 * @param {number} endLine The 1-based last line of the block.
 */
export function reveal(editorId, startLine, endLine) {
    const editor = window.blazorMonaco.editor.getEditor(editorId, true);
    const model = editor?.getModel();
    if (!model || model.isDisposed()) {
        return;
    }

    const range = new monaco.Range(startLine, 1, endLine, model.getLineMaxColumn(endLine));
    editor.setSelection(range);
    editor.revealRangeInCenter(range);
    editor.focus();
}

/**
 * Places the cursor at the end of the text, scrolls it into view and gives the editor the focus.
 * @param {string} editorId The id of the editor of BlazorMonaco.
 */
export function focusEnd(editorId) {
    const editor = window.blazorMonaco.editor.getEditor(editorId, true);
    const model = editor?.getModel();
    if (!model || model.isDisposed()) {
        return;
    }

    const lineNumber = model.getLineCount();
    editor.setPosition({ lineNumber, column: model.getLineMaxColumn(lineNumber) });
    editor.revealLine(lineNumber);
    editor.focus();
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
            case Name:
                decorations.push({ range, options: { description: "block-name", inlineClassName: "block-name" } });
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
// only undefined lets Monaco fall back to folding by indentation. Folds that an edit collapsed to one line give an
// empty list until the next update, so that indentation folds do not show up in between.
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

            return ranges;
        }
    });
}
