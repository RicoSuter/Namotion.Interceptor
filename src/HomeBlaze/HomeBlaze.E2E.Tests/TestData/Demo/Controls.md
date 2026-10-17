---
title: Controls Demo
---

# Controls Demo

This page is changed by the control widget tests.

:: HomeBlaze.Samples.Motor#ctlmotor Name=ControlMotor TargetSpeed=800 SimulationInterval=00:00:01
::

:: HomeBlaze.Components.Card#ctlcard Title="Switch Target"
The outline of this card is switched by a control.
::

:: HomeBlaze.Components.Switch#ctloutlineswitch Path=ctlcard/Outlined Label="Card outline"
::

:: HomeBlaze.Components.Switch#ctlpowerswitch Path=ctlmotor/IsPowered Label="Power"
::

:: HomeBlaze.Components.Switch#ctlopswitch Path=ctlmotor/IsPowered Label="Power by operation" On=ctlmotor/PowerOn Off=ctlmotor/PowerOff
::

:: HomeBlaze.Components.Switch#ctlblockedswitch Path=ctlmotor/IsPowered Label="Blocked power" On=ctlmotor/PowerOff Off=ctlmotor/PowerOn
::

:: HomeBlaze.Components.Switch#ctlconfirmswitch Path=ctlmotor/IsPowered Label="Power with stop" On=ctlmotor/PowerOn Off=ctlmotor/EmergencyStop
::

:: HomeBlaze.Components.Slider#ctlspeedslider Path=ctlmotor/TargetSpeed Label="Target speed"
::

:: HomeBlaze.Components.Slider#ctlloadslider Path=ctlmotor/Load
::

:: HomeBlaze.Components.Select#ctlmodeselect Path=ctlmotor/Mode Label="Motor mode"
::

:: HomeBlaze.Components.OperationButton#ctlstopbutton Path=ctlmotor/EmergencyStop
::

:: HomeBlaze.Components.OperationButton#ctlpoweronbutton Path=ctlmotor/PowerOn
::

:: HomeBlaze.Components.OperationButton#ctldiagnosticsbutton Path=ctlmotor/GetDiagnostics
::

:: HomeBlaze.Components.Control#ctlmodecontrol Path=ctlmotor/Mode Label="Control mode"
::

:: HomeBlaze.Components.Control#ctlspeedcontrol Path=ctlmotor/CurrentSpeed Label="Control speed"
::

:: HomeBlaze.Components.Slider#ctlwrongslider Path=ctlmotor/Missing Label="Wrong slider"
::
