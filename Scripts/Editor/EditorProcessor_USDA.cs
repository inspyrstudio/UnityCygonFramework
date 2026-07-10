// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine.Rendering;

namespace InspyrStudio.CygonLink
{
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
        
        [MenuItem("Tools/Cygon Link/Regenerate Materials", false, 11)]
        public static void RegenerateMaterials()
        {
            string[] usdaFiles = Directory.GetFiles(Application.dataPath, "*.usda", SearchOption.AllDirectories);

            HashSet<string> materialFolders = new HashSet<string>();
            List<string> sceneAssetPaths = new List<string>();

            foreach (string full in usdaFiles)
            {
                if (File.ReadLines(full).FirstOrDefault() != "#usda 1.0 | Cygon") continue;
                
                string matFolder = Path.Combine(Path.GetDirectoryName(full), "materials");
                if (Directory.Exists(matFolder)) materialFolders.Add(matFolder);
                
                sceneAssetPaths.Add(ToAssetPath(full));
            }
            
            int deleted = 0;
            foreach (string matFolder in materialFolders)
            {
                foreach (string mat in Directory.GetFiles(matFolder, "*.mat"))
                {
                    if (AssetDatabase.DeleteAsset(ToAssetPath(mat))) deleted++;
                }
            }
            
            AssetDatabase.Refresh();
            
            // Reimporting recreates the materials (via ProcessMaterials) and rebinds them on the meshes.
            foreach (string scene in sceneAssetPaths)
                AssetDatabase.ImportAsset(scene, ImportAssetOptions.ForceUpdate);
            
            EditorRuntime_USDA.SendLog("green", $"Materials regenerated: {deleted} deleted, {sceneAssetPaths.Count} USDA reimported.");
        }

        private static bool ProcessMaterials(string usdaPath)
        {
            bool createdNew = false;
            string usdaFolder = Path.GetDirectoryName(usdaPath);
            string materialsFolder = Path.Combine(usdaFolder, "materials");
            string texturesFolder = Path.Combine(usdaFolder, "textures");

            if (!Directory.Exists(materialsFolder)) Directory.CreateDirectory(materialsFolder);

            string rawText = File.ReadAllText(usdaPath);
            string[] materialBlocks = rawText.Split(new string[] { "def Material" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in materialBlocks)
            {
                if (!block.Contains("UsdPreviewSurface")) continue;
                
                Match nameMatch = Regex.Match(block, @"""([^""]+)""");
                if (!nameMatch.Success) continue;

                string matName = nameMatch.Groups[1].Value;
                string matPath = Path.Combine(materialsFolder, matName + ".mat").Replace('\\', '/');
                
                // Guard: never rebuild an existing material (also prevents an infinite reimport loop).
                if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null) continue;
                
                // Resolve shader + property names for the active render pipeline
                string shaderName = "Standard";
                string colorProp = "_Color", texProp = "_MainTex";
                string normalProp = "_BumpMap", heightProp = "_ParallaxMap";
                string metallicProp = "_Metallic", smoothnessProp = "_Glossiness", emissionProp = "_EmissionColor";
                
                if (GraphicsSettings.currentRenderPipeline != null)
                {
                    string rpName = GraphicsSettings.currentRenderPipeline.GetType().ToString();
                    if (rpName.Contains("Universal"))
                    {
                        shaderName = "Universal Render Pipeline/Lit";
                        colorProp = "_BaseColor"; texProp = "_BaseMap";
                        normalProp = "_BumpMap"; heightProp = "_ParallaxMap";
                        smoothnessProp = "_Smoothness";
                    }
                    else if (rpName.Contains("HDRenderPipeline") || rpName.Contains("HighDefinition"))
                    {
                        shaderName = "HDRP/Lit";
                        colorProp = "_BaseColor"; texProp = "_BaseColorMap";
                        normalProp = "_NormalMap"; heightProp = "_HeightMap";
                        smoothnessProp = "_Smoothness";
                    }
                }
                
                Shader shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    EditorRuntime_USDA.SendLog("red", $"Shader '{shaderName}' not found; skipping material '{matName}'.");
                    continue;
                }
                
                Material mat = new Material(shader);
                
                // UsdPreviewSurface scalar/color inputs (only set when explicitly authored)
                Color? diffuse = TryGetColor(block, "diffuseColor");
                if (diffuse.HasValue) mat.SetColor(colorProp, diffuse.Value);
                
                float? metallic = TryGetFloat(block, "metallic");
                if (metallic.HasValue) mat.SetFloat(metallicProp, Mathf.Clamp01(metallic.Value));
                
                // USD authors roughness; Unity Lit shaders expose smoothness (its inverse).
                float? roughness = TryGetFloat(block, "roughness");
                if (roughness.HasValue) mat.SetFloat(smoothnessProp, Mathf.Clamp01(1f - roughness.Value));
                
                Color? emissive = TryGetColor(block, "emissiveColor");
                if (emissive.HasValue && emissive.Value.maxColorComponent > 0f)
                {
                    mat.SetColor(emissionProp, emissive.Value);
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
                
                // UsdUVTexture options (applied to the imported texture assets)
                TextureWrapMode? wrap = ParseWrap(TryGetToken(block, "wrapS"));
                bool? sRGB = ParseColorSpace(TryGetToken(block, "sourceColorSpace"));
                
                // Textures (filename convention: _complete / _normal / _height)
                TryAssignTexture(mat, texProp, Path.Combine(texturesFolder, matName + "_complete"), false, wrap, sRGB);
                TryAssignTexture(mat, normalProp, Path.Combine(texturesFolder, matName + "_normal"), true, wrap, null);
                TryAssignTexture(mat, heightProp, Path.Combine(texturesFolder, matName + "_height"), false, wrap, false);
                
                // Enable keywords so the shader actually renders the maps
                if (mat.GetTexture(normalProp)) mat.EnableKeyword("_NORMALMAP");
                if (mat.GetTexture(heightProp)) mat.EnableKeyword("_PARALLAXMAP");
                
                Vector2 uvScale = TryGetVector2(block, "scale") ?? Vector2.one;
                Vector2 uvOffset = TryGetVector2(block, "translation") ?? Vector2.zero;
                mat.SetTextureScale(texProp, uvScale);
                mat.SetTextureOffset(texProp, uvOffset);
                
                float? rotation = TryGetFloat(block, "rotation");
                if (rotation.HasValue && Mathf.Abs(rotation.Value) > 0.0001f)
                    EditorRuntime_USDA.SendLog("orange", $"Material '{matName}': UV rotation ({rotation.Value}) is not applied (URP/Standard Lit has no built-in UV rotation).");
                
                AssetDatabase.CreateAsset(mat, matPath);
                createdNew = true;
            }

            if (createdNew)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return createdNew;
        }

        //=============================================================================
        // USD VALUE PARSING HELPERS
        //=============================================================================

        private static Color? TryGetColor(string block, string inputName)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(inputName) + @"\s*=\s*\(([^)]+)\)");
            if (!m.Success) return null;
            string[] c = m.Groups[1].Value.Split(',');
            if (c.Length < 3) return null;
            return new Color(ParseF(c[0]), ParseF(c[1]), ParseF(c[2]), c.Length >= 4 ? ParseF(c[3]) : 1f);
        }
        private static Vector2? TryGetVector2(string block, string inputName)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(inputName) + @"\s*=\s*\(([^)]+)\)");
            if (!m.Success) return null;
            string[] c = m.Groups[1].Value.Split(',');
            if (c.Length < 2) return null;
            return new Vector2(ParseF(c[0]), ParseF(c[1]));
        }
        private static float? TryGetFloat(string block, string inputName)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(inputName) + @"\s*=\s*(-?[\d.]+(?:[eE][+-]?\d+)?)");
            if (!m.Success) return null;
            return ParseF(m.Groups[1].Value);
        }
        private static string TryGetToken(string block, string inputName)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(inputName) + @"\s*=\s*""([^""]+)""");
            return m.Success ? m.Groups[1].Value : null;
        }
        private static float ParseF(string s)
        {
            return float.Parse(s.Trim(), CultureInfo.InvariantCulture);
        }
        private static TextureWrapMode? ParseWrap(string usdWrap)
        {
            if (string.IsNullOrEmpty(usdWrap)) return null;
            switch (usdWrap)
            {
                case "repeat": return TextureWrapMode.Repeat;
                case "mirror": return TextureWrapMode.Mirror;
                case "clamp": return TextureWrapMode.Clamp;
                case "black": return TextureWrapMode.Clamp;
                default: return null;
            }
        }
        private static bool? ParseColorSpace(string usdColorSpace)
        {
            if (string.IsNullOrEmpty(usdColorSpace)) return null;
            switch (usdColorSpace)
            {
                case "sRGB": return true;
                case "raw": return false;
                default: return null;
            }
        }

        //=============================================================================
        // TEXTURE ASSIGNMENT
        //=============================================================================

        private static void TryAssignTexture(Material mat, string propName, string basePath, bool isNormalMap, TextureWrapMode? wrapMode = null, bool? sRGB = null)
        {
            // Try common extensions
            string[] extensions = { ".png", ".jpg", ".tga", ".jpeg" };
            foreach (var ext in extensions)
            {
                string fullPath = (basePath + ext).Replace('\\', '/');
                if (File.Exists(fullPath))
                {
                    ConfigureTextureImport(fullPath, isNormalMap, wrapMode, sRGB);

                    Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(fullPath);
                    if (tex != null)
                    {
                        mat.SetTexture(propName, tex);
                        return;
                    }
                }
            }
        }
        private static void ConfigureTextureImport(string assetPath, bool isNormalMap, TextureWrapMode? wrapMode, bool? sRGB)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;
            
            bool changed = false;
            
            if (isNormalMap && importer.textureType != TextureImporterType.NormalMap)
            {
                importer.textureType = TextureImporterType.NormalMap;
                changed = true;
            }
            
            if (wrapMode.HasValue && importer.wrapMode != wrapMode.Value)
            {
                importer.wrapMode = wrapMode.Value;
                changed = true;
            }
            
            if (!isNormalMap && sRGB.HasValue && importer.sRGBTexture != sRGB.Value)
            {
                importer.sRGBTexture = sRGB.Value;
                changed = true;
            }
            
            if (changed) importer.SaveAndReimport();
        }
        private static string ToAssetPath(string absolutePath)
        {
            string p = absolutePath.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            return p.StartsWith(data) ? "Assets" + p.Substring(data.Length) : p;
        }
    }
}