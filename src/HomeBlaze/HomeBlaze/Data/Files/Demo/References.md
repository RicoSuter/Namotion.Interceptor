---
title: References Demo
navTitle: References Demo
position: 8
---

# References Demo

This page shows how a page refers to subjects: by the name of a block, by a relative path and by an absolute path. It also shows what a reference cannot show, so the messages in the last two sections are expected.

## A name at every depth

The motor below has the name `refmotor`. A name is known on the whole page, so the same expression works directly on the page, inside a card and inside a tab inside that card.

~~~~markdown
:: HomeBlaze.Samples.Motor#refmotor Name="Reference Motor" TargetSpeed=1500 SimulationInterval=00:00:01
::

On the page: {{ refmotor/CurrentSpeed }} RPM.

:: HomeBlaze.Components.Card Title="One level down" Icon=Link
Inside the card: {{ refmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Components.TabLayout
:::: Tab#depth Title="Two levels down"
Inside the tab inside the card: {{ refmotor/CurrentSpeed }} RPM.
::::
:::
::
~~~~

:: HomeBlaze.Samples.Motor#refmotor Name="Reference Motor" TargetSpeed=1500 SimulationInterval=00:00:01
::

On the page: {{ refmotor/CurrentSpeed }} RPM.

:: HomeBlaze.Components.Card Title="One level down" Icon=Link
Inside the card: {{ refmotor/CurrentSpeed }} RPM.

::: HomeBlaze.Components.TabLayout
:::: Tab#depth Title="Two levels down"
Inside the tab inside the card: {{ refmotor/CurrentSpeed }} RPM.
::::
:::
::

## Three ways to refer to a subject

A `Widget` block shows a subject that exists somewhere else. The three widgets below show a motor each, and each `Path` has another form.

By name: `Path=refmotor` is the name of a block on this page, here the motor of the section above.

:: HomeBlaze.Components.Widget Path=refmotor
::

By relative path: `Path=../Conveyor` starts at this page, where `../` is the folder of the page, so it leads to the file `Conveyor.json` next to the page.

:: HomeBlaze.Components.Widget Path=../Conveyor
::

By absolute path: `Path=/Demo/Conveyor` starts at the root, so it leads to the same motor from every page in every folder.

:: HomeBlaze.Components.Widget Path=/Demo/Conveyor
::

## Paths in expressions

An expression takes the same paths as a `Widget`, followed by the name of a property.

~~~~markdown
Relative: the conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.

Absolute: the conveyor has a temperature of {{ /Demo/Conveyor/Temperature }} °C.
~~~~

Relative: the conveyor runs at {{ ../Conveyor/CurrentSpeed }} RPM.

Absolute: the conveyor has a temperature of {{ /Demo/Conveyor/Temperature }} °C.

## References that contain themselves

A `Widget` cannot show a block that contains it, because the page would then show itself without end. The message "Cannot show path" in the three cards below is expected.

The first card refers to itself:

~~~~markdown
:: HomeBlaze.Components.Card#selfcard Title="Self Card"
::: HomeBlaze.Components.Widget Path=selfcard
:::
::
~~~~

:: HomeBlaze.Components.Card#selfcard Title="Self Card"
::: HomeBlaze.Components.Widget Path=selfcard
:::
::

The next two cards refer to each other. Each card shows the other one, which shows the first one once more, and there the round ends with the message:

~~~~markdown
:: HomeBlaze.Components.Card#firstcard Title="First Card"
::: HomeBlaze.Components.Widget Path=secondcard
:::
::

:: HomeBlaze.Components.Card#secondcard Title="Second Card"
::: HomeBlaze.Components.Widget Path=firstcard
:::
::
~~~~

:: HomeBlaze.Components.Card#firstcard Title="First Card"
::: HomeBlaze.Components.Widget Path=secondcard
:::
::

:: HomeBlaze.Components.Card#secondcard Title="Second Card"
::: HomeBlaze.Components.Widget Path=firstcard
:::
::

## A path that leads nowhere

A `Widget` whose path leads to no subject shows the message "Cannot resolve path". The message below is expected, because there is no motor of that name next to this page.

:: HomeBlaze.Components.Widget Path=../NoSuchMotor
::

An expression with such a path shows nothing. Between the two arrows is the speed of the motor that does not exist: → {{ ../NoSuchMotor/CurrentSpeed }} ←
