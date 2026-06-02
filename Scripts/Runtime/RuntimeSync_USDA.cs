using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

[InitializeOnLoad]
public class RuntimeSync_USDA
{
    private static FileSystemWatcher projectWatcher;
    static RuntimeSync_USDA()
    {
        // Initialize watcher for the Assets folder
        string assetsPath = Application.dataPath;
        projectWatcher = new FileSystemWatcher(assetsPath);
        
        projectWatcher.IncludeSubdirectories = true;
        projectWatcher.Filter = "*.usda";
        projectWatcher.NotifyFilter = NotifyFilters.LastWrite;

        // Hook into the change event
        projectWatcher.Changed += OnUsdaFileChanged;
        projectWatcher.EnableRaisingEvents = true;
        
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

        EditorRuntime_USDA.SendLog("white", "Waiting for changes in sources files");
    }
    
    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        RefreshAll();
    }

    private static void OnUsdaFileChanged(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher runs on a background thread. 
        // We must move back to the Main Thread for Unity API calls.
        EditorApplication.delayCall += () => 
        {
            RefreshSceneInstances(e.FullPath);
        };
    }
    
    [MenuItem("Tools/Cygon (UCF)/Force Refresh %&r", false, 10)]
    public static void ManualRefreshAll()
    {
        EditorRuntime_USDA.SendLog("orange", "Manual Refresh Triggered...");
        RefreshAll();
    }
    private static void RefreshAll()
    {
        // Search the physical disk for all .usda files within the Assets folder
        // This is more reliable than FindAssets for custom extensions.
        string[] files = Directory.GetFiles(Application.dataPath, "*.usda", SearchOption.AllDirectories);

        if (files.Length == 0)
        {
            EditorRuntime_USDA.SendLog("yellow", "Nothing related to .usda files found to refresh.");
            return;
        }

        foreach (string fullPath in files)
        {
            RefreshSceneInstances(fullPath);
        }

        EditorRuntime_USDA.SendLog("green", $"Forced refresh complete. Processed {files.Length} files.");
    }

    private static void RefreshSceneInstances(string fullPath)
    {
        // Fix: Convert System Path to Unity Project Path correctly
        string assetPath = fullPath.Replace("\\", "/"); // Normalize slashes
        string dataPath = Application.dataPath.Replace("\\", "/");

        if (assetPath.StartsWith(dataPath))
        {
            // Subtract the data path and add "Assets" back to get the local path
            assetPath = "Assets" + assetPath.Substring(dataPath.Length);
        }
        else
        {
            // Fallback: If it's outside the data path for some reason, try to find it via GUID
            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath); 
        }

        // Now check if it exists in Unity's eyes
        var assetEntry = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (assetEntry == null)
        {
            EditorRuntime_USDA.SendLog("red", $"File detected but Unity can't find it at: {assetPath}.");
            return;
        }

        EditorRuntime_USDA.SendLog("orange", $"Trying to refresh : {assetPath}...");

        // 1. Force the import
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        // 2. Load the prefab
        GameObject updatedAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (updatedAsset == null) return;

        // 3. Update scene objects
        GameObject[] allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        string fileName = Path.GetFileNameWithoutExtension(assetPath);

        for (int i = 0; i < allObjects.Length; i++)
        {
            if (allObjects[i] == null)
                break;
            
            // Check if the object name matches the file or the common Unity (Clone) suffix
            if (allObjects[i] != null && allObjects[i].name == fileName || allObjects[i].name == fileName + "(Clone)")
            {
                UpdateInstance(allObjects[i], updatedAsset);
            }
        }
    }

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
        
        // 1. If it's a prefab instance, revert it to clear "Play Mode" junk/overrides
        if (PrefabUtility.IsPartOfAnyPrefab(instance)) 
        { 
            // Revert ensures the instance is a "valid" clone of the disk asset again 
            PrefabUtility.RevertPrefabInstance(instance, InteractionMode.AutomatedAction);
        }

        EditorRuntime_USDA.SendLog("green", $"Refreshed instance {instance.name}.");
    }
}