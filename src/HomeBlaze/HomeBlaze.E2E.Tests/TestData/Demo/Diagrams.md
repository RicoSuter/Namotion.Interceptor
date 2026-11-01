---
title: Diagrams Demo
---

# Diagrams Demo

```mermaid
graph TD
    Pump --> Tank
```

```mermaid
this is not a diagram
```

```mermaid
%%{init: {"securityLevel": "loose", "htmlLabels": true, "flowchart": {"htmlLabels": true}}}%%
graph TD
    A["<img src='http://mermaid-exfiltration.test/exfiltrate.png'> Label"] --> B
    click A "http://mermaid-exfiltration.test/clicked" "Tooltip"
    click B call alert()
```

```mermaid
stateDiagram-v2
    Space --> Comment
    Comment --> Set
    Set --> Joined
    style Joined mask-image:url(//mermaid-control.test/joined)
    style Space mask-image:url (//mermaid-exfiltration.test/space)
    style Comment mask-image:url/**/(//mermaid-exfiltration.test/comment)
    style Set mask-image:image-set ("//mermaid-exfiltration.test/set" 1x)
```
