# Requirements
Before using Cygon Link, make sure the following requirements are met.

## Software Requirements
| Software | Minimum Version | Notes                                                       |
|----------|-----------------|-------------------------------------------------------------|
| Unity    | 6000.1.4f1+     | Not guaranteed to work on earlier versions                  |
| Cygon    | 0.3.3+          | Earlier versions do not support the required export pipeline |

[//]: # (> **New to Cygon?** Watch the [Cygon installation tutorial on YouTube]&#40;https://www.youtube.com/watch?v=SaS8J_4AumM&#41;, and see [What is Cygon?]&#40;WhatIsCygon.md&#41; for a quick overview of the product.)
> New to Cygon? See [What is Cygon?](WhatIsCygon.md).

## Render Pipeline
Cygon Link generates materials for the **active render pipeline** and is tested with the **Universal Render Pipeline (URP)**. It also detects the Built-in and HDRP pipelines and selects the matching Lit shader, though URP is the primary target.

## No extra packages required
Unlike some USD workflows, Cygon Link does **not** depend on Unity's USD package or any third-party library. It ships with its own Scripted Importer and file watcher, so there is nothing else to install or enable.

## Platform Support
| Platform       | Supported          |
|----------------|--------------------|
| Windows 64-bit | ✅                 |
| macOS          | ✅ *(editor-only, lightly tested)* |
| Linux          | ❔ *(untested)*    |

> Cygon Link is an **editor-only** tool: it runs while you work in the Unity Editor and does not add any runtime code to your builds.
