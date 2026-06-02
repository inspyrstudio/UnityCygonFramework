using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine.Rendering;

public class EditorProcessor_USDA : AssetPostprocessor
{
    void OnPreprocessAsset()
    {
        if (assetPath.EndsWith(".usda"))
        {
            // Read the first line
            string firstLine = File.ReadLines(assetPath).FirstOrDefault();

            // If it's NOT a Cygon file, force the default Unity USD importer
            if (firstLine == "#usda 1.0 | Cygon")
            {
                // Force it to use YOUR importer for Cygon files
                AssetDatabase.SetImporterOverride<EditorImporter_USDA>(assetPath);
            }
            else
            {
                EditorRuntime_USDA.SendLog("white", "USDA is not a Cygon file, skipping custom import...");
                AssetDatabase.ClearImporterOverride(assetPath);
            }
        }
    }
    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        List<string> usdasToReimport = new List<string>();

        foreach (string path in importedAssets)
        {
            if (path.ToLower().EndsWith(".usda"))
            {
                if (ProcessMaterials(path)) 
                {
                    usdasToReimport.Add(path);
                }
            }
        }

        // Re-import the USDA files ONLY if we just created their materials for the first time.
        // This fixes the "Pink" issue by letting the Importer see the new .mat files.
        foreach (string usdaPath in usdasToReimport)
        {
            AssetDatabase.ImportAsset(usdaPath, ImportAssetOptions.ForceUpdate);
        }
    }
    private static bool ProcessMaterials(string usdaPath)
    {
        bool createdNew = false;
        string usdaFolder = Path.GetDirectoryName(usdaPath);
        string materialsFolder = Path.Combine(usdaFolder, "materials");
        string texturesFolder = Path.Combine(usdaFolder, "textures");

        if (!Directory.Exists(materialsFolder)) Directory.CreateDirectory(materialsFolder);

        string rawText = File.ReadAllText(usdaPath);
        string[] materialBlocks = rawText.Split(new string[] { "def Material" }, System.StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in materialBlocks)
        {
            Match nameMatch = Regex.Match(block, @"""([^""]+)""");
            if (!nameMatch.Success) continue;

            string matName = nameMatch.Groups[1].Value;
            string matPath = Path.Combine(materialsFolder, matName + ".mat").Replace('\\', '/');

            if (AssetDatabase.LoadAssetAtPath<Material>(matPath) == null)
            {
                // Property Mapping based on Pipeline
                string shaderName = "Standard";
                string colorProp = "_Color", texProp = "_MainTex";
                string normalProp = "_BumpMap", heightProp = "_ParallaxMap";

                if (GraphicsSettings.currentRenderPipeline != null)
                {
                    string rpName = GraphicsSettings.currentRenderPipeline.GetType().ToString();
                    if (rpName.Contains("Universal")) {
                        shaderName = "Universal Render Pipeline/Lit";
                        colorProp = "_BaseColor"; texProp = "_BaseMap";
                        normalProp = "_BumpMap"; heightProp = "_ParallaxMap";
                    } else if (rpName.Contains("HDRP")) {
                        shaderName = "HDRP/Lit";
                        colorProp = "_BaseColor"; texProp = "_BaseColorMap";
                        normalProp = "_NormalMap"; heightProp = "_HeightMap";
                    }
                }

                Material mat = new Material(Shader.Find(shaderName));

                // 1. Color assignment
                Match colorMatch = Regex.Match(block, @"color3f inputs:diffuseColor = \(([^)]+)\)");
                if (colorMatch.Success)
                {
                    string[] c = colorMatch.Groups[1].Value.Split(',');
                    mat.SetColor(colorProp, new Color(
                        float.Parse(c[0].Trim(), CultureInfo.InvariantCulture),
                        float.Parse(c[1].Trim(), CultureInfo.InvariantCulture),
                        float.Parse(c[2].Trim(), CultureInfo.InvariantCulture)));
                }

                // 2. Automated Texture Suffix Search
                // We look for: matName_complete, matName_normal, matName_height
                TryAssignTexture(mat, texProp, Path.Combine(texturesFolder, matName + "_complete"), false);
                TryAssignTexture(mat, normalProp, Path.Combine(texturesFolder, matName + "_normal"), true);
                TryAssignTexture(mat, heightProp, Path.Combine(texturesFolder, matName + "_height"), false);

                // Enable keywords so the shader actually renders the maps
                if (mat.GetTexture(normalProp)) mat.EnableKeyword("_NORMALMAP");
                if (mat.GetTexture(heightProp)) mat.EnableKeyword("_PARALLAXMAP");

                AssetDatabase.CreateAsset(mat, matPath);
                createdNew = true;
            }
        }

        if (createdNew)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        return createdNew;
    }
    private static void TryAssignTexture(Material mat, string propName, string basePath, bool isNormalMap)
    {
        // Try common extensions
        string[] extensions = { ".png", ".jpg", ".tga", ".jpeg" };
        foreach (var ext in extensions)
        {
            string fullPath = basePath + ext;
            if (File.Exists(fullPath))
            {
                //string assetPath = "Assets" + fullPath.Substring(Application.dataPath.Length).Replace('\\', '/');
                
                if (isNormalMap) FixNormalMapImportSettings(fullPath);

                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(fullPath);
                if (tex != null)
                {
                    mat.SetTexture(propName, tex);
                    return;
                }
            }
        }
    }
    private static void FixNormalMapImportSettings(string assetPath)
    {
        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null && importer.textureType != TextureImporterType.NormalMap)
        {
            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();
        }
    }
}