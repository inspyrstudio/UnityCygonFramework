# Installation

[//]: # (> **New to Cygon?** Before installing the plugin, make sure Cygon is installed and running on your machine. Watch the [Cygon installation tutorial on YouTube]&#40;https://www.youtube.com/watch?v=SaS8J_4AumM&#41; to get started.)
> New to Cygon? See [What is Cygon?](WhatIsCygon.md).

## Option 1 - Via Fab (Recommended)
On Fab, Cygon Link is distributed as a single Unity package.
- Go to the [Cygon Link page on Fab](https://www.fab.com/) *(soon)*.
- Add the plugin to your Library and download it, you get a single `.unitypackage` file.
- In Unity, open **Assets → Import Package → Custom Package…**, select the downloaded `.unitypackage`, and click **Import**.
- The plugin is installed under `Assets/CygonLink/`.

## Option 2 — Via Git URL (Package Manager)
Install the latest version straight from the repository:
- Open the Unity Package Manager (**Window → Package Manager**).
- Click the **+** button and select **Add package from git URL…**
- Paste the following URL:
  ```
  https://github.com/inspyrstudio/CygonLink.git
  ```
- Click **Add**. Unity downloads and compiles the package automatically.

## Verifying the Installation
After installation:
- Confirm **Cygon Link** appears in the Package Manager (Git URL install) or under `Assets/CygonLink/` (Fab install).
- A **Tools → Cygon Link** menu appears in the editor menu bar (*Force Refresh* and *Regenerate Materials*).
- Drop the sample scene from `Docs/Samples/` into your Project window to verify the plugin intercepts and imports it correctly.

> For importing your own scenes from Cygon, see the [First Import Guide](FirstImport.md).
