# Mermaid diagrams in Markdown

Render ```` ```mermaid ```` fenced code blocks as diagrams in pages, metadata notes and agent replies. The implementation stays small: one server-side change in the agent renderer, one script, one CSS rule.

## Syntax

Only the info string `mermaid` (case-insensitive) is a diagram, as on GitHub, GitLab and in Markdig's diagrams extension. `mermaidjs` and other aliases are not supported, so a page renders the same in HomeBlaze and elsewhere.

## Server side

- **Pages and notes**: no change. `MarkdownPageParser` uses `UseAdvancedExtensions()`, whose diagrams extension already emits `<pre class="mermaid">source</pre>`, and `MetadataNotes` builds on the same pipeline.
- **Agent replies**: `AgentMarkdownRenderer` clears the classes of every fenced code block today. It keeps doing that, except that a fenced block whose info string is `mermaid` and that has a closing fence is rendered as `<pre class="mermaid">` with its source HTML-encoded. The class is a fixed value, not text copied from the reply. A fence without a closing fence stays `<pre><code>` without a class.
- **Streaming** needs no change: `AgentStreamingBubble` only moves text before a line start outside a fenced block into its stable segments, so an open mermaid fence stays in the tail as code and becomes a diagram once closed.

## Client side

Files in `HomeBlaze/wwwroot`:

- `lib/mermaid/mermaid.min.js`: the UMD build of the latest Mermaid 11.x release at implementation time, checked in as is (its header keeps the version). It is only fetched when a page shows a diagram.
- `mermaid-diagrams.js`: loaded with a `<script>` tag in `App.razor` after `blazor.web.js`.
- CSS in `app.css`.

`mermaid-diagrams.js`:

1. Holds a live `document.getElementsByClassName('mermaid')` collection and a `MutationObserver` on `document.body` with `childList` and `subtree` only, so text-only updates of live values do not call it. It also scans once at startup.
2. On each call, every `pre.mermaid` element without `data-mermaid-state` gets `data-mermaid-state="pending"` synchronously, before the browser paints, and its source text is read.
3. If the cache, a `Map` from theme plus source to SVG markup, holds the diagram, the SVG replaces the content of the element right away and the state becomes `done`. This keeps diagrams from blinking when Blazor re-inserts the markup of a page segment because a live value in it changed.
4. Otherwise the script loads `mermaid.min.js` once (a single shared promise), initializes Mermaid and renders the diagram with `mermaid.parse` and `mermaid.render`, fills the cache and sets the state to `done`.
5. If loading, parsing or rendering fails, the state becomes `error` and the source stays in the element as text.

The theme is `dark` when an element with the `homeblaze-dark-mode` class (set by `MainLayout`) exists, otherwise `default`, read when a diagram renders. Diagrams already on the page keep their theme when dark mode is toggled until the page renders them again.

CSS:

- `pre.mermaid[data-mermaid-state="pending"]` is hidden. A `pre.mermaid` without a state (script not running) or with `error` shows its source like a code block.
- No sizing rule: Mermaid's default `useMaxWidth` already renders the SVG with `width="100%"` and its natural width as `max-width`, so a diagram fits narrow notes.

## Security

Agent replies are untrusted, and the agent renderer makes sure that nothing loads without a click. One configuration applies to all diagrams:

```js
mermaid.initialize({
  startOnLoad: false,
  securityLevel: 'strict',
  htmlLabels: false,
  flowchart: { htmlLabels: false },
  suppressErrorRendering: true,
  theme,
  secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'suppressErrorRendering', 'maxEdges',
           'htmlLabels', 'flowchart', 'theme', 'themeVariables', 'themeCSS', 'fontFamily', 'altFontFamily', 'dompurifyConfig'],
});
```

- `strict` encodes HTML in labels and turns off `click` handlers and links.
- `htmlLabels: false` renders labels as SVG text instead of HTML in a `foreignObject`, so a label cannot load an image.
- `secure` stops an `%%{init: ...}%%` directive or frontmatter config in the diagram from overriding these keys, including `themeCSS` and the font keys, whose CSS could load a URL.

Pages lose `click` links and HTML labels in diagrams. Agents still cannot add a diagram to a page through a write, because `MarkdownWriteRules` treats it as active content.

## Tests

Unit tests in `HomeBlaze.AI.Tests`:

- A closed `mermaid` fence renders as `<pre class="mermaid">` with encoded source, including for `MERMAID`.
- A fence without a closing fence, a `mermaidjs` fence and other languages render as `<pre><code>` without a class.
- `</pre><script>` in a mermaid fence is encoded.
- In streaming, an open mermaid fence stays code in the tail.

E2E tests in `HomeBlaze.E2E.Tests/MermaidDiagramTests.cs`:

- A page with a diagram shows an `svg` inside `pre.mermaid`.
- A page with an invalid diagram shows its source.
- A diagram with an `<img src="http://...">` label, a `click` line and an `init` directive asking for `loose` renders no `img`, `a` or `foreignObject` element and no `on*` attribute, and the browser sends no request to that URL.
- A timeline agent reply with a diagram shows an `svg`.
