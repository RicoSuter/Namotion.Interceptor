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
