---
title: Cards Demo
---

# Cards Demo

:: HomeBlaze.Components.Card Title="Sentence Card"
One sentence in the body.
::

:: HomeBlaze.Components.Card Title="Fixed Card" Height=150
First paragraph of a body that does not fit.

Second paragraph.

Third paragraph.

Fourth paragraph.

Fifth paragraph.

::: Footer
Footer of the fixed card
::

:: HomeBlaze.Components.Card Title="Ratio Card" AspectRatio="2:1"
Body of the ratio card.
::

:: HomeBlaze.Components.Card Title="Invalid Ratio Card" AspectRatio="2/1"
Body of the invalid ratio card.
::

:: HomeBlaze.Components.GridLayout#cardgrid Rows=1 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.Card Title="Grid Card"
::

:: HomeBlaze.Components.CanvasLayout#cardcanvas
::: Node X=20 Y=20 Width=300 Height=200
:::: HomeBlaze.Components.Card Title="Canvas Card"
::
