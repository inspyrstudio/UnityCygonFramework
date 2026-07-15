# What is Cygon?

Cygon is a **standalone 3D environment prototyping tool** developed by us, [Inspyr Studio](https://www.cygon.tech/about-us). It is designed to help artists and designers iterate rapidly on 3D environment layouts — a dedicated blockout tool that keeps the creative process fast, with less overhead.
![Cygon interface](Screenshots/Cygon_SampleView.png)

Cygon Link is the bridge that brings your Cygon blockouts directly into Unity, keeping both in sync as you iterate.
![Cygon to Unity pipeline](Screenshots/Cygon_Pipline.png)

## Key capabilities relevant to Cygon Link
- Exports 3D scenes in the **USDA** (Universal Scene Description ASCII) format.
- Organizes exported scenes as a hierarchy: a top-level `.usda` file referencing individual mesh files inside a `meshes/` subfolder, plus a `textures/` folder used by the materials.
- Supports quick export via **CTRL + S** or the export button in the Project Manager.

## Getting Cygon
> **Note:** Cygon is currently in **beta**. Access requires a paid subscription, available on the [Cygon pricing page](https://www.cygon.tech/pricing).

[//]: # (> Once you have access, watch the [Cygon installation tutorial on YouTube]&#40;https://www.youtube.com/watch?v=SaS8J_4AumM&#41; to get up and running.)

## Minimum version
Cygon **0.3.3** or higher is required for Cygon Link to work correctly. Earlier versions do not support the export pipeline expected by the plugin.

## How Cygon and Cygon Link work together
When you export a scene from Cygon, it writes a `.usda` file to disk. Cygon Link watches for those files inside your Unity project's `Assets/` folder and automatically imports them through its custom Scripted Importer converting geometry, materials, and transforms into native Unity GameObjects.

For the full workflow, see [Getting Started](GettingStarted.md).
