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

    // The SVG by theme and source, so that a diagram Blazor inserts again is shown before the browser paints.
    const svgs = new Map();
    let mermaidLoad = null;
    let renderCount = 0;

    // The Monaco loader defines a global AMD define, which makes the bundle register itself as a module instead of
    // setting window.mermaid, so the define is hidden while the script runs.
    const loadMermaid = () => mermaidLoad ??= new Promise((resolve, reject) => {
        const amdDefine = window.define;
        window.define = undefined;
        const restore = () => window.define = amdDefine;

        const script = document.createElement('script');
        script.src = 'lib/mermaid/mermaid.min.js';
        script.onload = () => {
            restore();
            resolve(window.mermaid);
        };
        script.onerror = error => {
            restore();
            reject(error);
        };
        document.head.appendChild(script);
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

    const show = (element, svg) => {
        element.innerHTML = svg;
        element.dataset.mermaidState = 'done';
    };

    const render = async (element, source, theme, key) => {
        try {
            const mermaid = await loadMermaid();
            mermaid.initialize({ ...settings, theme });
            if (!await mermaid.parse(source, { suppressErrors: true })) {
                element.dataset.mermaidState = 'error';
                return;
            }

            const { svg } = await mermaid.render(`mermaid-diagram-${++renderCount}`, source);
            const safeSvg = withoutLinks(svg);
            svgs.set(key, safeSvg);
            show(element, safeSvg);
        } catch {
            element.dataset.mermaidState = 'error';
        }
    };

    const scan = () => {
        if (diagrams.length === 0) {
            return;
        }

        const theme = document.querySelector('.homeblaze-dark-mode') ? 'dark' : 'default';

        // A copy, as a rendered SVG can contain elements with the class too.
        for (const element of Array.from(diagrams)) {
            if (element.tagName !== 'PRE' || element.dataset.mermaidState) {
                continue;
            }

            const source = element.textContent;
            const key = theme + '\n' + source;
            const svg = svgs.get(key);
            if (svg) {
                show(element, svg);
            } else {
                element.dataset.mermaidState = 'pending';
                render(element, source, theme, key);
            }
        }
    };

    // Only added and removed nodes: a live value that changes the text of a node does not call it.
    new MutationObserver(scan).observe(document.body, { childList: true, subtree: true });
    scan();
})();
