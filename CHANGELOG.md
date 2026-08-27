# Changelog

All notable changes to this project will be documented in this file.

## [0.1.0]

### Added

- Initial release of the Cygon Unity Importer.
- Support for USDA file importing.

### Changed

- The minimum supported editor version is now **Unity 6000.1.4f1**. Compatibility with versions before this is not guaranteed.

### Fixed

- Textures and materials are now created in the correct sub-folders upon import.

## [0.1.1-preview]

### Added ###

- Better logs with color and informations display.
- Tool for manual refresh in _[Tools/Cygon (UCF)/Force Refresh]_, available with `CTRL + ALT + R`

### Changed ###

- Renamed scripts and assemblies definitions

### Fixed ###

- `RefreshAll` method was not refreshing correctly with the shortcut

## [0.1.2-preview]

### Added ###

- `EditorRuntime_USDA`, a static class with information for custom logs.

### Fixed ###

- `Cygon Link` name was not the same everywhere.

## [0.1.3-preview]

### Added ###

- `EditorPostProcessor_USDA`, a static `AssetPostProcessor` that auto imports materials, finds textures , 
make them a normal map if needed and select the material shader correclty based on current `Graphics Pipeline`.
This currently works only for BaseMap, NormalMap and HeightMap

### Fixed ###

- Error on `AssetDatabase.Refresh()` or `AssetDatabase.SaveAssets()` by using `EditorPostProcessor_USDA`.

## [0.1.4-preview]

### Changed ###

- `EditorPostProcessor_USDA`, to `EditorProcessor_USDA` because it now handles default and custom importer based
on infos found in the usda.

## [0.1.5-preview]

### Changed ###

- All scripts are now declared within the `InspyrStudio.CygonLink` namespace.
- Editor and Runtime assembly definitions are now constrained to the **Editor** platform, so editor-only code is no longer compiled into player builds.

### Fixed ###

- Imported meshes rendered inside-out (only back-faces visible). Triangle winding now respects the source `leftHanded` orientation, so faces display correctly.

## [0.1.6]

### Added ###

- Multi-material meshes: geometry partitioned with USD `GeomSubset` is now imported as one submesh per subset, each bound to its own material. Previously a multi-material object received only a single material.
- Add Documentation

### Changed ###

- The shared `misc` assembly is now constrained to the **Editor** platform (editor and runtime assemblies already were), so no plugin code is compiled into player builds.
- Removed the unused `Unity.VisualScripting` dependency (assembly references and `using`) and dead code in the importer (unused color/texture lookup caches and their helper).
- Material textures (base color, normal, displacement) are now resolved from the USD material graph via the surface shader's `inputs:` connections instead of by filename convention`.

### Fixed ###

- A stray `World.mat` was generated from the scene's non-material content; only real `def Material` blocks are processed now.

## [0.1.7]

### Fixed ###
- Objects were placed at the wrong world position, most visibly losing the height of the floor they belong to (Y snapping to 0), and sometimes X/Z as well. The hierarchy stack pushed a frame only when a block introduced a GameObject but popped on every closing brace, so each `over` block (used for per-face material bindings) leaked one level. The stack drifted upward and later prims were parented to an ancestor, losing their parent's offset. Every block now pushes exactly one frame, so pushes and pops always balance.
- Transform operations are now tracked per (object, operation) pair instead of `instanceID + 1/2/3` keys in a shared set, where objects with nearby instance IDs could collide and have an `xformOp` silently skipped. The `xformOpOrder` declaration line is also ignored explicitly rather than being parsed as a value.

## [0.1.8]

### Added ###

- Support for the library export, where meshes live once in a `class` prim and instances point at them with an internal reference such as `prepend references = </World/Meshes/Wall>`. A referenced mesh is built once and shared by every instance that uses it, and `class` prims are skipped by the hierarchy pass so a library spawns no objects of its own. Material bindings are also inherited from ancestor prims, as USD intends: this export binds the material on the prim whose child carries the reference, so a binding and its mesh no longer have to sit on the same prim. Together with the two earlier layouts, a `references` line now resolves to an external file (`@meshes/Wall.usda@`) or to a prim inside the same file, and meshes may still be declared inline.
- Support for the single-file export, where meshes are embedded in the scene USDA as inline `def Mesh` prims and their per-face materials are bound by the `GeomSubset` prims themselves. Such a scene used to import as one merged mesh with no materials, because any file holding geometry was taken for a standalone mesh file. A mesh prim's own transform is honoured too, so geometry offset from its pivot keeps that offset (a rotation or scale there, which the exporter does not currently write, is reported rather than silently dropped). Scenes that reference separate mesh files keep working.

### Changed ###

- Entering and leaving play mode no longer reimports every `.usda` in the project. The play-mode hook ignored which transition had occurred and force-reimported every file on all four of them. Returning to edit mode now only re-applies the files that actually changed while playing, and re-applies them without reimporting. Live updates while in play mode are unaffected: they come from the file watcher, not from this hook.
- `Force Refresh` now only touches Cygon `.usda` files instead of every `.usda` in the project.
- A prim-level `rel material:binding` now targets the prim whose block declares it rather than the last mesh instance seen, which the single-file export only establishes after the binding line.
- `xformOp:rotateZXY` is recognized alongside `xformOp:rotateZYX`; the single-file export writes the former, and an unmatched name silently dropped the rotation.

### Fixed ###
- Rotations were wrong on any object not rotated purely around the up axis. Converting from USD (right-handed, Y up) to Unity (left-handed) mirrors Z, which negates the angles about X and Y and leaves the angle about Z alone; the importer negated Y and Z instead. Rotations around Y only came out the same either way, which is why props lying flat looked correct while tilted ones did not. The wrong conversion is older than this release but stayed invisible: earlier exports only wrote zero rotations, and the importer recognised `xformOp:rotateZYX` alone.