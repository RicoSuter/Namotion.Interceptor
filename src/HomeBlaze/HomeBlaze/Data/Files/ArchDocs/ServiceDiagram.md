---
title: Service Diagram
---

# Service Diagram

The [Orders service](services/Orders/Service.md) and [Payments service](services/Payments/Service.md) are shown here as positioned service widgets. Each box references the same service used by the [overview](Architecture.md) and [release page](operations/Releasing.md).

:: HomeBlaze.Components.CanvasLayout#services MinHeight=280 Width=740 MinWidth=640 MaxWidth=740 Scale=FitWidth IsSnapToGridEnabled=true GridSize=20
::: Node#orders X=20 Y=20 Width=320 Height=220
:::: HomeBlaze.Components.Widget#ordersWidget Path=/ArchDocs/services/Orders/Service.md/service
::::
:::
::: Node#payments X=400 Y=20 Width=320 Height=220
:::: HomeBlaze.Components.Widget#paymentsWidget Path=/ArchDocs/services/Payments/Service.md/service
::::
:::
::
