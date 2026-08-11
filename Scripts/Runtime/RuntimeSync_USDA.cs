// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

namespace InspyrStudio.CygonLink
{
    /// <summary>
    /// Editor auto-sync: watches the Assets folder for .usda changes and refreshes any matching
    /// scene instances (also runs on play-mode transitions and via a menu command).
    /// </summary>
    [InitializeOnLoad] public class RuntimeSync_USDA
    {
        //=============================================================================
        // VARIABLES
        //=============================================================================

        #region VARIABLES

        private static FileSystemWatcher projectWatcher;

        #endregion



        //=============================================================================
        // INITIALIZATION
        //=============================================================================

        #region INITIALIZATION

        /// <summary>Sets up the file watcher and the play-mode hook when the editor loads.</summary>
        static RuntimeSync_USDA()
        {
            InitializeWatcher();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorRuntime_USDA.SendLog("white", "Watching source files for changes.");
        }

        /// <summary>Watches the Assets folder for *.usda writes so edits made outside Unity sync back in.</summary>
        private static void InitializeWatcher()
        {
            projectWatcher = new FileSystemWatcher(Application.dataPath)
            {
                IncludeSubdirectories = true,
                Filter = "*.usda",
                NotifyFilter = NotifyFilters.LastWrite
            };
            
            projectWatcher.Changed += OnUsdaFileChanged;
            projectWatcher.EnableRaisingEvents = true;
        }

        #endregion



        //=============================================================================
        // REFRESH
        //=============================================================================

        #region REFRESH

        /// <summary>Menu command — force a re-import + scene refresh of every .usda under Assets.</summary>
        [MenuItem("Tools/Cygon Link/Force Refresh %&r", false, 10)]
        public static void ManualRefreshAll()
        {
            EditorRuntime_USDA.SendLog("orange", "Manual Refresh Triggered...");
            RefreshAll();
        }

        /// <summary>Re-imports every .usda file under Assets and refreshes its scene instances.</summary>
        private static void RefreshAll()
        {
            // Search the physical disk for all .usda files within the Assets folder.
            // This is more reliable than FindAssets for custom extensions.
            string[] files = Directory.GetFiles(Application.dataPath, "*.usda", SearchOption.AllDirectories);
            
            if (files.Length == 0)
            {
                EditorRuntime_USDA.SendLog("yellow", "No .usda files found to refresh.");
                return;
            }
            
            foreach (string fullPath in files)
            {
                RefreshSceneInstances(fullPath);
            }
            
            EditorRuntime_USDA.SendLog("green", $"Forced refresh complete. Processed {files.Length} files.");
        }

        /// <summary>Re-imports one USDA and updates every matching scene GameObject.</summary>
        /// <param name="fullPath">Absolute disk path of the changed .usda file.</param>
        private static void RefreshSceneInstances(string fullPath)
        {
            string assetPath = CygonUsda.ToAssetPath(fullPath);
            
            // Make sure Unity actually knows about this asset before touching it.
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null)
            {
                EditorRuntime_USDA.SendLog("red", $"File detected but Unity can't find it at: {assetPath}.");
                return;
            }
            
            EditorRuntime_USDA.SendLog("orange", $"Refreshing {assetPath}...");
            
            // 1. Force the import
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            
            // 2. Load the prefab
            GameObject updatedAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (updatedAsset == null) return;
            
            // 3. Update matching scene objects
            RefreshMatchingInstances(Path.GetFileNameWithoutExtension(assetPath), updatedAsset);
        }

        /// <summary>Updates every scene GameObject whose name matches the asset (or its "(Clone)" variant).</summary>
        /// <param name="fileName">The asset file name (without extension) to match against object names.</param>
        /// <param name="sourceAsset">The freshly imported asset whose children are copied onto each match.</param>
        private static void RefreshMatchingInstances(string fileName, GameObject sourceAsset)
        {
            GameObject[] allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            string cloneName = fileName + "(Clone)";
            
            foreach (GameObject go in allObjects)
            {
                if (go == null) continue;
                if (go.name == fileName || go.name == cloneName)
                {
                    UpdateInstance(go, sourceAsset);
                }
            }
        }

        #endregion



        //=============================================================================
        // INSTANCE UPDATE
        //=============================================================================

        #region INSTANCE UPDATE

        /// <summary>Rebuilds an instance's children from the freshly imported source asset.</summary>
        /// <param name="instance">The scene GameObject to refresh in place.</param>
        /// <param name="sourceAsset">The imported asset to copy children from.</param>
        private static void UpdateInstance(GameObject instance, GameObject sourceAsset)
        {
            // Record for Undo system
            Undo.RegisterCompleteObjectUndo(instance, $"{EditorRuntime_USDA.logPrefix} RuntimeSync Update");
            
            // Clear children
            List<GameObject> children = new List<GameObject>();
            foreach (Transform child in instance.transform) children.Add(child.gameObject);
            foreach (GameObject child in children) Object.DestroyImmediate(child);
            
            // Re-spawn from the fresh import
            foreach (Transform child in sourceAsset.transform)
            {
                GameObject newChild = Object.Instantiate(child.gameObject, instance.transform);
                newChild.name = child.name;
            }
            
            // If it's a prefab instance, revert it to clear "Play Mode" junk/overrides
            if (PrefabUtility.IsPartOfAnyPrefab(instance))
            {
                // Revert ensures the instance is a "valid" clone of the disk asset again
                PrefabUtility.RevertPrefabInstance(instance, InteractionMode.AutomatedAction);
            }
            
            EditorRuntime_USDA.SendLog("green", $"Refreshed instance {instance.name}.");
        }

        #endregion



        //=============================================================================
        // CALLBACKS
        //=============================================================================

        #region CALLBACKS

        /// <summary>Refreshes everything when entering or exiting play mode.</summary>
        /// <param name="state">The play-mode transition reported by the editor.</param>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            RefreshAll();
        }

        /// <summary>Watcher callback (fires on a background thread) — marshals the refresh onto the main thread.</summary>
        /// <param name="sender">The <see cref="FileSystemWatcher"/> that raised the event.</param>
        /// <param name="e">Event data containing the changed file's full path.</param>
        private static void OnUsdaFileChanged(object sender, FileSystemEventArgs e)
        {
            // FileSystemWatcher runs on a background thread.
            // We must move back to the Main Thread for Unity API calls.
            EditorApplication.delayCall += () =>
            {
                RefreshSceneInstances(e.FullPath);
            };
        }

        #endregion


    }
}