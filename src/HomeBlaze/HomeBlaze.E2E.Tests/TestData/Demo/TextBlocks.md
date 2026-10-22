---
title: Text Blocks
---

# Golden Text

A paragraph with *emphasis*, **strong text**, `code` and a [link](https://example.com).
A second line of the same paragraph.

The target speed is {{ ../TextBlocksMotor/TargetSpeed }} RPM, and a [reference link][golden] stands next to it.

## A Heading

A Setext Heading
----------------

- First item
- Second item
  - Nested item

1. One
2. Two

A list that starts at three:

3. Three
4. Four

> A quote
> on two lines.

```csharp
var answer = 42;
```

```
A fenced block without a language.
```

An indented code block:

    var indented = true;

| Name | Value |
|------|-------|
| A    | 1     |

---

<div class="golden-note">
Raw HTML.
</div>

Text with a footnote[^1].

:: HomeBlaze.Components.Card Title="Golden Card"
Text inside the card with the target speed {{ ../TextBlocksMotor/TargetSpeed }} RPM.

- A list inside the card
::

A paragraph directly before a card.
:: HomeBlaze.Components.Card Title="Adjacent Card"
Text of the adjacent card.
::
A paragraph directly after a card.

Closing paragraph.

[golden]: https://example.com/reference
[^1]: The footnote.
