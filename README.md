# Cygon Link for Unity
Seamless integration and live-sync for USDA files.

Cygon Link is a powerful bridge that provides a robust pipeline for bringing USDA (Universal Scene Description) assets into Unity. It features a custom Scripted Importer for automated conversion and a Live Reloader that syncs changes from Cygon to Unity in real-time.

## Key Features
- **📦 Automated Prefab Generation** — Converts USDA hierarchies, meshes, and materials directly into native Unity GameObjects with collision.
- **🔥 Live Hot-Reloading** — Detects file saves in Cygon and instantly updates all instances in your active Unity scene (even in Play Mode).
- **🛠️ Intelligent Mesh Processing** — Includes a "Weld Vertices" pass and normal-correction logic to eliminate dark artifacts and shadow leaks, plus per-face materials via USD `GeomSubset`.
- **🎨 Material Management** — Automatically generates materials in a local `materials/` folder for the active render pipeline (URP tested).

## Documentation

Full documentation is available in the [`Docs/`](Docs/) folder:

| Guide                                     | Description                                          |
|-------------------------------------------|------------------------------------------------------|
| [Getting Started](Docs/GettingStarted.md) | Overview and the recommended setup order             |
| [What is Cygon?](Docs/WhatIsCygon.md)     | Quick overview of the Cygon tool itself              |
| [Requirements](Docs/Requirements.md)      | Software versions and render pipeline                |
| [Installation](Docs/Installation.md)      | Installing via Fab or a Git URL                      |
| [First Import](Docs/FirstImport.md)       | Step-by-step guide for your first Cygon scene import |
| [Live Sync](Docs/LiveSync.md)             | Setting up real-time sync between Cygon and Unity    |

A sample `.usda` scene is included at `Docs/Samples/` to verify the plugin works without needing Cygon installed.

## Getting Started

### Installation via Fab (Pre-compiled)
The easiest way to get started is to grab the plugin from Fab, where it is distributed as a single Unity package.

- Go to the [Cygon Link page on Fab](https://www.fab.com/) *(soon)*.
- Add it to your Library and download the `.unitypackage`.
- In Unity, open **Assets → Import Package → Custom Package…**, select the file, and click **Import**.

### Installation via Git URL
If you prefer to pull the latest version straight from source:

- Open the Unity Package Manager (**Window > Package Manager**).
- Click the **+** button and select **Add package from git URL...**
- Paste the following URL:
  ```
  https://github.com/inspyrstudio/CygonLink.git
  ```

## How to use it?
1. **Import your assets**. Drag your `.usda` file along with its `meshes/` and `textures/` folders into the Unity Project window (or export from Cygon directly into `Assets/`). The importer automatically creates a `materials/` sub-folder to store the generated `.mat` files.
2. **Add to Scene**. Drag the imported USDA asset from the Project window into your Hierarchy or Scene view.
3. **Live Editing Workflow**. Keep Unity open (works in both Edit and Play Mode). Open the source file in Cygon, modify geometry or transforms, and Export / Quick Export with **CTRL + S**.
4. **Switch back to Unity**. The auto-sync triggers an orange log in the Console and your objects update instantly. You can also force it with **Tools → Cygon Link → Force Refresh** (**CTRL + ALT + R**).

## Dependencies
This plugin has no third-party code or libraries, and needs no additional Unity packages. It ships with its own Scripted Importer and file watcher.

## Miscellaneous
- **Requirements**
  - Unity Version: **6000.1.4f1** or higher. *(The package is not guaranteed to work before 6000.1.4f1, but you are welcome to test it.)*
  - Cygon Version: **0.3.3** or higher. *(Versions before 0.3.3 do not export in the format required for Unity import and will not work.)*
- **Contributing**

  Contributions are welcome! Please feel free to tell us about anything that doesn't work with the package on the [Discord](https://discord.gg/E5awVaqRdc).
