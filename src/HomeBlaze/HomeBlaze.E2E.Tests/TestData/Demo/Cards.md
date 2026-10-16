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
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.Card Title="Grid Ratio Card" AspectRatio="1:2"
::

:: HomeBlaze.Components.CanvasLayout#cardcanvas
::: Node X=20 Y=20 Width=300 Height=200
:::: HomeBlaze.Components.Card Title="Canvas Card"
::: Node X=340 Y=20 Width=300 Height=200
:::: HomeBlaze.Components.Card Title="Canvas Ratio Card" AspectRatio="1:2"
::

:: HomeBlaze.Components.GridLayout#hiddenblockgrid Rows=1 Columns=2
::: Cell Row=0 Column=0
:::: HomeBlaze.Components.ValueMap#hiddenblockgridfirstmap
::::: Case Value=On Text="On"
::::
:::: HomeBlaze.Components.Card Title="Grid Card After Hidden Block"
::::
::: Cell Row=0 Column=1
:::: HomeBlaze.Components.Card Title="Grid Card Before Hidden Block"
::::
:::: HomeBlaze.Components.ValueMap#hiddenblockgridlastmap
::::: Case Value=On Text="On"
::

:: HomeBlaze.Components.CanvasLayout#hiddenblockcanvas
::: Node X=20 Y=20 Width=300 Height=200
:::: HomeBlaze.Components.ValueMap#hiddenblockcanvasmap
::::: Case Value=On Text="On"
::::
:::: HomeBlaze.Components.Card Title="Canvas Card After Hidden Block"
::
