---
title: Page Contributions and Link Previews
navTitle: Page Contributions and Link Previews
status: Planned
---

# Page Contributions and Link Previews [Planned]

Expose richer related information around ordinary documentation without requiring embedded blocks.

## Implementation readiness

**Delivery:** Requires the HomeBlaze extensions identified below; standalone JSON subjects and explicitly embedded widgets can be delivered independently.

**Prerequisites:** New page-shell contribution slots, ordered multi-contributor lookup, tracking/disposal, and Markdown link-preview integration.

## Extension distinction [Planned]

Existing widget content slots belong inside a configured widget. They do not implement page-level actions/context/additional-view contributions. Existing component lookup resolves one registration; its enumeration does not yet provide inherited multi-contributor discovery. Rich previews on ordinary links also need renderer integration.

## Scope [Proposed]

Settle slot names, ordering, selection behavior, and link activation/accessibility in the UI design. A specialized subject widget or standalone detail page can be delivered before this work.

## Detailed behavior [Planned]

Regular Markdown remains the primary authoring experience for documentation and designs. Generic metadata can make richer information available without widget blocks, a custom document type, or layout directives in the source. An ordinary MarkdownFile can define a Service through `$service` or reference a standalone service through a `documents` relationship. Both retain normal Markdown rendering and can offer the service's code and deployment information. UI contributions can cover several defined subjects without making one the page's implicit identity. Explicit widget blocks remain an optional way to place a widget at a specific point in the authored content.

The user proposed additional slots where subject-specific UI can contribute around the normal page. Extend the existing component-registration model with page-level contribution points. Proposed initial slots are:

| Slot | Purpose | Example |
|---|---|---|
| Page actions | Contextual commands in the page toolbar or menu | Explore selected projects or refresh analysis |
| Context panel | Optional information alongside the document | Related subjects, authored relationships, host summary, deployments |
| Additional views | Alternate views reachable from the page | Service details or deployment matrix |

Optional positions above or below the content can host a summary later if that placement is wanted. Exact names and presentation remain builder and visual-design decisions. Slots are in the application page frame, so the Markdown body keeps its normal renderer and portable source. These UI extension points complement the existing `[Slot]` authoring mechanism, which fills widget content from Markdown blocks; declaring a UI extension does not require an author to add a content-slot block.

UI modules register contributions based on the current subject's type or interfaces and can test relevant metadata and resolved related subjects. An ordinary page with relationship metadata can therefore receive a context contribution without `$type`. Domain subjects expose facts and capabilities; their UI assemblies provide the components. Metadata describes meaning and relationships, not component class names, UI layout, or arbitrary executable rendering instructions.

Support multiple applicable contributors with deterministic ordering and stable identities. Empty or inapplicable contributions occupy no space. Contributions refresh when the metadata or the facts they read change, and release subscriptions and resources when the page or contribution leaves. Visibility and placement are application/user preferences unless an author explicitly embeds a widget. An extension failure should leave the document readable and identify the failed contribution rather than replace the whole page.

The current component registry resolves one Page/Edit/Setup/Widget component, with inheritance/interface fallback; its GetComponents enumeration only matches the exact subject type. Reuse the registration and context patterns, while defining ordered multi-contributor discovery for the new slots. Do not assume the existing single-component lookup already implements those semantics. Keep page-specific contribution dependencies in the UI layers rather than Namotion.Interceptor.Utils.

### Markdown link previews

The user confirmed that the context slot provides information without authored blocks and also wants ordinary Markdown links to act as entry points to richer information. When a link resolves to a subject, offer an overlay card with its title, subtitle, description, and applicable information. The Markdown source remains a standard link, with normal navigation preserved. This presentation does not turn a document reference into a typed architectural dependency.

Proposed default: provide a generic subject preview with an explicit open action and allow subject UI modules to contribute a compact summary through the same registration patterns. Reuse subject presentation data between previews and the context slot. Pointer hover and keyboard focus should expose the preview; touch needs an accessible explicit preview interaction. Unresolved or external links retain ordinary link behavior. Preview errors must not prevent navigation. Exact gesture behavior and card layout remain visual-design decisions; ordinary images keep their image semantics.

The user emphasized that rich, useful UI is part of the feature. Compact previews limit the initial footprint, not the information available: offer relevant facts and navigation into the context slot or an applicable additional view. Context slots, previews, service summaries, and deployment matrices are intended capabilities; their first-iteration boundary will be selected after the sample review. Proposed default: compose reusable subject presentation and contributions across these surfaces, with progressive disclosure for larger lists and details. Service UI makes selected hosts/projects, resources, relationships, and deployments explorable; deployment UI makes units, environments, manual versions, and links explorable. Provide clear loading, empty, unresolved, and error states and preserve the provenance of displayed facts. Exact styling and interactions remain visual-design decisions.

## Related specifications

[Overview and status legend](../overview.md) · [Platform readiness](../implementation-readiness.md)
