# Live Sync Workflow

Cygon Link supports **real-time synchronization** between Cygon and Unity. When you modify and re-export a scene in Cygon, the corresponding objects in your open Unity scene update automatically, no manual reimport needed. This works in both **Edit Mode and Play Mode**.

## Setup
- Follow the [First Import](FirstImport.md) guide to import your scene (the files must live under `Assets/`).
- Drag the imported asset into your scene.
- Keep the Unity editor open.

> **No editor setting required.** Cygon Link ships with its own file watcher that monitors your project's `Assets/` folder and triggers the reimport automatically.

## Live Editing Loop
Once set up, the workflow is:
- Open your scene in **Cygon**.
- Make changes, modify geometry or transforms.
- Export with **CTRL + S** or the Cygon export button.
- Switch back to **Unity**. Cygon Link detects the changed `.usda` file, logs an orange message in the Console, and updates every matching instance in your scene instantly.

## Manual refresh
If you ever need to force it, use the menu:
- **Tools → Cygon Link → Force Refresh** *(shortcut **CTRL + ALT + R**)*, reimports every Cygon `.usda` under `Assets/` and refreshes the matching scene instances.
- **Tools → Cygon Link → Regenerate Materials**, deletes and rebuilds every generated material from the current USDA (useful after changing material settings in Cygon, since existing `.mat` files are otherwise left untouched).
