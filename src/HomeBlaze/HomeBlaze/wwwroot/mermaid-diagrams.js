// Renders the Mermaid diagrams of Markdown, which the server writes as <pre class="mermaid"> with the source as text, as
// SVG. Blazor inserts a new element whenever the markup around a diagram changes, so the page is watched instead of
// components calling in. Mermaid itself is only loaded once a page shows a diagram.
(() => {
    // One configuration for every diagram, as the text of an agent reply is not trusted: strict turns off click handlers,
    // labels as SVG text cannot load an image, and secure stops an init directive or frontmatter of a diagram from
    // changing these keys, including the CSS ones, whose url() could load an address.
    const settings = {
        startOnLoad: false,
        securityLevel: 'strict',
        htmlLabels: false,
        flowchart: { htmlLabels: false },
        suppressErrorRendering: true,
        secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'suppressErrorRendering', 'maxEdges',
            'htmlLabels', 'flowchart', 'theme', 'themeVariables', 'themeCSS', 'fontFamily', 'altFontFamily', 'dompurifyConfig']
    };

    const diagrams = document.getElementsByClassName('mermaid');

    // By theme and source: the SVG, null for an error, or the pending render, so that a diagram Blazor inserts again
    // is shown before the browser paints and the tail of a streaming reply is rendered once.
    const results = new Map();
    let mermaidLoad = null;

    // Mermaid prefixes the ids of an SVG and scopes its style with the render id, which a remembered SVG holds as a
    // placeholder so that every copy gets ids of its own; the closing x keeps one id from being the prefix of another.
    const idPlaceholder = 'mermaid-diagram-id';
    let idCount = 0;
    const nextId = () => `mermaid-diagram-${++idCount}x`;

    // The Monaco loader defines a global AMD define, which makes the bundle register itself as a module instead of
    // setting window.mermaid. The code runs as an inline script, which executes synchronously, so define is only hidden
    // while it runs and not while Monaco may load a module of its own.
    const loadMermaid = () => mermaidLoad ??= (async () => {
        const response = await fetch('lib/mermaid/mermaid.min.js');
        if (!response.ok) {
            throw new Error(`Mermaid could not be loaded: ${response.status}`);
        }

        const code = await response.text();
        const amdDefine = window.define;
        window.define = undefined;
        try {
            const script = document.createElement('script');
            script.textContent = code;
            document.head.appendChild(script);
            script.remove();
        } finally {
            window.define = amdDefine;
        }

        if (!window.mermaid) {
            throw new Error('Mermaid did not load.');
        }

        return window.mermaid;
    })().catch(error => {
        mermaidLoad = null;
        throw error;
    });

    // Strict still draws a click statement with a URL as a link, so the links are removed and their content stays.
    const withoutLinks = svg => {
        const template = document.createElement('template');
        template.innerHTML = svg;
        for (const link of template.content.querySelectorAll('a')) {
            link.replaceWith(...link.childNodes);
        }

        return template.innerHTML;
    };

    const remember = (key, result) => {
        results.set(key, result);
        if (results.size > 100) {
            results.delete(results.keys().next().value);
        }
    };

    const show = (element, svg) => {
        if (svg === null) {
            element.dataset.mermaidState = 'error';
            return;
        }

        element.innerHTML = svg.replaceAll(idPlaceholder, nextId());
        element.dataset.mermaidState = 'done';
    };

    // A failed load rejects instead of returning null, so it is not remembered and a later diagram tries again.
    const render = async (source, theme) => {
        const mermaid = await loadMermaid();
        try {
            mermaid.initialize({ ...settings, theme });
            if (!await mermaid.parse(source, { suppressErrors: true })) {
                return null;
            }

            const id = nextId();
            const { svg } = await mermaid.render(id, source);
            return withoutLinks(svg).replaceAll(id, idPlaceholder);
        } catch {
            return null;
        }
    };

    const scan = () => {
        if (diagrams.length === 0) {
            return;
        }

        // Not inside an SVG, where a diagram can set any class on its nodes.
        const theme = document.querySelector('.homeblaze-dark-mode:not(svg *)') ? 'dark' : 'default';

        // A copy, as a rendered SVG can contain elements with the class too.
        for (const element of Array.from(diagrams)) {
            if (element.tagName !== 'PRE' || element.dataset.mermaidState) {
                continue;
            }

            const source = element.textContent;
            const key = theme + '\n' + source;
            let result = results.get(key);
            if (result === undefined) {
                result = render(source, theme).then(
                    svg => {
                        remember(key, svg);
                        return svg;
                    },
                    () => {
                        results.delete(key);
                        return null;
                    });
                remember(key, result);
            }

            if (result instanceof Promise) {
                element.dataset.mermaidState = 'pending';
                result.then(svg => show(element, svg));
            } else {
                show(element, result);
            }
        }
    };

    // Only added and removed nodes: a live value that changes the text of a node does not call it.
    new MutationObserver(scan).observe(document.body, { childList: true, subtree: true });
    scan();
})();
