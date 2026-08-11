# Getting Started with Cygon Link
Cygon Link is a bridge between **Cygon** and **Unity**. [Cygon](WhatIsCygon.md) is a standalone 3D environment blockout and prototyping tool. Cygon Link lets you bring your blockouts into Unity and see changes reflected instantly, eliminating the traditional export/import friction.

## What does it do?
- **Automated Prefab Generation**. Converts USDA scene hierarchies exported by Cygon directly into native Unity GameObjects: meshes (with colliders), materials, and transforms.
- **Live Hot-Reloading**. Detects file saves on disk and automatically reimports and updates all instances in your active scene (Edit or Play Mode).
- **Intelligent Mesh Processing**. Applies a vertex-welding pass and normal-correction so imported geometry lights cleanly, plus per-face materials via USD `GeomSubset`.

## Before you start
Make sure you have the following ready:

| Software | Minimum Version |
|----------|-----------------|
| Unity    | 6000.1.4f1+     |
| Cygon    | 0.3.3+          |

[//]: # (> New to Cygon? See [What is Cygon?]&#40;WhatIsCygon.md&#41; and watch the [installation tutorial on YouTube]&#40;https://www.youtube.com/watch?v=SaS8J_4AumM&#41;.)
> New to Cygon? See [What is Cygon?](WhatIsCygon.md).

## Step-by-step guides
Follow these in order for your first setup:

1. [Requirements](Requirements.md). Check software versions and the render pipeline
2. [Installation](Installation.md). Install Cygon Link via Fab or a Git URL
3. [First Import](FirstImport.md). Import your first Cygon scene into Unity
4. [Live Sync](LiveSync.md). Set up real-time sync between Cygon and Unity

## How it works
- Export your scene from Cygon into your project's `Assets/` folder with **CTRL + S**.
- Switch to Unity, the scene is imported automatically into the Project window.
- Drag the asset into your scene.
- Make changes in Cygon, export again, Unity updates instantly.

For a deeper explanation of the pipeline, see [What is Cygon?](WhatIsCygon.md).

## Dependencies
Cygon Link has no third-party dependencies and needs no additional Unity packages, it ships with its own Scripted Importer and file watcher.
