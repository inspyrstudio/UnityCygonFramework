// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

namespace InspyrStudio.CygonLink
{
    /// <summary>
    /// Editor auto-sync: watches the Assets folder for .usda changes and refreshes any matching
    /// scene instances. Returning to edit mode re-applies whatever changed while playing, and a menu
    /// command forces a full refresh.
    /// </summary>
    [InitializeOnLoad] public class RuntimeSync_USDA
    {
        //=============================================================================
        // VARIABLES
        //=============================================================================

        #region VARIABLES

        private static FileSystemWatcher projectWatcher;

        /// <summary>SessionState key holding the files changed during play mode, one path per line.</summary>
        private const string PendingSyncKey = "CygonLink.PendingSceneSync";

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

        /// <summary>Re-imports every Cygon .usda file under Assets and refreshes its scene instances</summary>
        private static void RefreshAll()
        {
            // Scanning the disk is more reliable than FindAssets for custom extensions. Only Cygon files are touched, so unrelated .usda assets are left alone.
            List<string> files = CygonUsda.FindAll();
            
            if (files.Count == 0)
            {
                EditorRuntime_USDA.SendLog("yellow", "No Cygon .usda files found to refresh.");
                return;
            }
            
            foreach (string fullPath in files)
            {
                RefreshSceneInstances(fullPath);
            }
            
            EditorRuntime_USDA.SendLog("green", $"Forced refresh complete. Processed {files.Count} files.");
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
            
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            SyncSceneInstances(assetPath);
        }

        /// <summary>
        /// Re-applies an already-imported asset onto its scene instances, without reimporting it.
        /// Used when the asset on disk is known to be up to date and only the scene has gone stale.
        /// </summary>
        /// <param name="assetPath">Project-relative path of the imported USDA asset</param>
        private static void SyncSceneInstances(string assetPath)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null) return;
            
            RefreshMatchingInstances(Path.GetFileNameWithoutExtension(assetPath), asset);
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
        // PENDING SYNC
        //=============================================================================

        #region PENDING SYNC

        /// <summary>
        /// Remembers a file whose scene instances will need re-applying. Kept in
        /// <see cref="SessionState"/> rather than a static field because both entering and leaving
        /// play mode reload the domain, which resets static state.
        /// </summary>
        /// <param name="assetPath">Project-relative path of the changed USDA asset</param>
        private static void AddPendingSync(string assetPath)
        {
            List<string> pending = LoadPendingSync();
            if (pending.Contains(assetPath)) return;
            
            pending.Add(assetPath);
            SessionState.SetString(PendingSyncKey, string.Join("\n", pending));
        }

        /// <summary>Reads the files awaiting a scene re-apply.</summary>
        /// <returns>The pending asset paths; empty when there is nothing to re-apply</returns>
        private static List<string> LoadPendingSync()
        {
            string raw = SessionState.GetString(PendingSyncKey, string.Empty);
            
            return string.IsNullOrEmpty(raw) ? new List<string>() : new List<string>(raw.Split('\n'));
        }

        #endregion



        //=============================================================================
        // CALLBACKS
        //=============================================================================

        #region CALLBACKS

        /// <summary>
        /// Re-applies the files that changed during play mode once the editor is back in edit mode.
        /// Leaving play mode restores the pre-play scene, which discards the updates applied while
        /// playing; the assets themselves were already imported, so nothing is reimported here.
        /// </summary>
        /// <param name="state">The play-mode transition reported by the editor</param>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            
            List<string> pending = LoadPendingSync();
            if (pending.Count == 0) return;
            
            foreach (string assetPath in pending) SyncSceneInstances(assetPath);
            
            SessionState.EraseString(PendingSyncKey);
            EditorRuntime_USDA.SendLog("green", $"Re-applied {pending.Count} file(s) changed during play mode.");
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
                if (EditorApplication.isPlaying) AddPendingSync(CygonUsda.ToAssetPath(e.FullPath));
                
                RefreshSceneInstances(e.FullPath);
            };
        }

        #endregion


    }
}