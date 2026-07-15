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

## [0.1.6-preview]

### Added ###

- Multi-material meshes: geometry partitioned with USD `GeomSubset` is now imported as one submesh per subset, each bound to its own material. Previously a multi-material object received only a single material.

### Changed ###

- The shared `misc` assembly is now constrained to the **Editor** platform (editor and runtime assemblies already were), so no plugin code is compiled into player builds.
- Removed the unused `Unity.VisualScripting` dependency (assembly references and `using`) and dead code in the importer (unused color/texture lookup caches and their helper).

### Fixed ###

- A stray `World.mat` was generated from the scene's non-material content; only real `def Material` blocks are processed now.