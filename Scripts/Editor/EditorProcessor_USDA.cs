// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine.Rendering;

namespace InspyrStudio.CygonLink
{
    /// <summary>
    /// Post-import pipeline for Cygon USDA files: forces the custom importer override and generates
    /// Unity materials from the USDA <c>UsdPreviewSurface</c> graph.
    /// </summary>
    public class EditorProcessor_USDA : AssetPostprocessor
    {
        //=============================================================================
        // IMPORT HOOKS
        //=============================================================================

        #region IMPORT HOOKS

        /// <summary>
        /// Routes Cygon USDA files to <see cref="EditorImporter_USDA"/> and lets Unity's default
        /// USD importer handle every other .usda file.
        /// </summary>
        void OnPreprocessAsset()
        {
            if (!assetPath.EndsWith(".usda")) return;

            if (CygonUsda.IsCygonFile(assetPath))
            {
                AssetDatabase.SetImporterOverride<EditorImporter_USDA>(assetPath);
            }
            else
            {
                EditorRuntime_USDA.SendLog("white", "USDA is not a Cygon file, skipping custom import...");
                AssetDatabase.ClearImporterOverride(assetPath);
            }
        }

        /// <summary>
        /// Generates materials for every imported USDA, then reimports the ones whose materials were
        /// just created so the importer can bind the new .mat files (fixes the initial "pink" pass).
        /// </summary>
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            List<string> usdasToReimport = new List<string>();
            
            foreach (string path in importedAssets)
            {
                if (path.ToLower().EndsWith(".usda") && ProcessMaterials(path))
                {
                    usdasToReimport.Add(path);
                }
            }
            
            foreach (string usdaPath in usdasToReimport)
            {
                AssetDatabase.ImportAsset(usdaPath, ImportAssetOptions.ForceUpdate);
            }
        }

        #endregion



        //=============================================================================
        // MENU
        //=============================================================================

        #region MENU

        /// <summary>
        /// Deletes every generated material next to a Cygon USDA and reimports, rebuilding them from
        /// scratch with the current interpreter. Needed because <see cref="ProcessMaterials"/> never
        /// overwrites an existing .mat, so code changes are otherwise invisible.
        /// </summary>
        [MenuItem("Tools/Cygon Link/Regenerate Materials", false, 11)]
        public static void RegenerateMaterials()
        {
            HashSet<string> materialFolders = new HashSet<string>();
            List<string> sceneAssetPaths = new List<string>();
            
            foreach (string full in CygonUsda.FindAll())
            {
                string matFolder = Path.Combine(Path.GetDirectoryName(full), "materials");
                if (Directory.Exists(matFolder)) materialFolders.Add(matFolder);

                sceneAssetPaths.Add(CygonUsda.ToAssetPath(full));
            }
            
            int deleted = 0;
            foreach (string matFolder in materialFolders)
            {
                foreach (string mat in Directory.GetFiles(matFolder, "*.mat"))
                {
                    if (AssetDatabase.DeleteAsset(CygonUsda.ToAssetPath(mat))) deleted++;
                }
            }
            
            AssetDatabase.Refresh();
            
            // Reimporting recreates the materials (via ProcessMaterials) and rebinds them on the meshes.
            foreach (string scene in sceneAssetPaths)
                AssetDatabase.ImportAsset(scene, ImportAssetOptions.ForceUpdate);
            
            EditorRuntime_USDA.SendLog("green", $"Materials regenerated: {deleted} deleted, {sceneAssetPaths.Count} USDA reimported.");
        }

        #endregion



        //=============================================================================
        // MATERIAL GENERATION
        //=============================================================================

        #region MATERIAL GENERATION

        /// <summary>Shader and shader-property names to target for the active render pipeline.</summary>
        private struct PipelineProfile
        {
            public string ShaderName;
            public string Color;
            public string BaseMap;
            public string Normal;
            public string Height;
            public string Metallic;
            public string Smoothness;
            public string Emission;
        }

        /// <summary>
        /// Creates one Unity material per <c>def Material</c> block in the USDA that does not already
        /// have a .mat on disk.
        /// </summary>
        /// <param name="usdaPath">Asset path of the USDA being processed.</param>
        /// <returns>True if at least one new material was created.</returns>
        private static bool ProcessMaterials(string usdaPath)
        {
            string usdaFolder = Path.GetDirectoryName(usdaPath);
            string materialsFolder = Path.Combine(usdaFolder, "materials");
            string texturesFolder = Path.Combine(usdaFolder, "textures");
            
            if (!Directory.Exists(materialsFolder)) Directory.CreateDirectory(materialsFolder);
            
            string[] materialBlocks = File.ReadAllText(usdaPath).Split(new string[] { "def Material" }, StringSplitOptions.RemoveEmptyEntries);
            
            bool createdNew = false;
            foreach (string block in materialBlocks)
            {
                // Skip non-material blocks. Split() also yields the scene content that precedes the
                // first "def Material"; without this guard its first quoted name became a bogus .mat.
                if (!block.Contains("UsdPreviewSurface")) continue;
                
                Match nameMatch = Regex.Match(block, @"""([^""]+)""");
                if (!nameMatch.Success) continue;
                
                string matName = nameMatch.Groups[1].Value;
                string matPath = Path.Combine(materialsFolder, matName + ".mat").Replace('\\', '/');
                
                // Guard: never rebuild an existing material (also prevents an infinite reimport loop).
                // Use "Tools > Cygon Link > Regenerate Materials" to force a rebuild.
                if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null) continue;
                
                Material mat = BuildMaterial(block, matName, texturesFolder);
                if (mat == null) continue;
                
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

        /// <summary>Builds a Unity material from a single USD <c>def Material</c> block.</summary>
        /// <returns>The created material, or null if the pipeline's shader could not be found.</returns>
        private static Material BuildMaterial(string block, string matName, string texturesFolder)
        {
            PipelineProfile profile = ResolvePipelineProfile();
            
            Shader shader = Shader.Find(profile.ShaderName);
            if (shader == null)
            {
                EditorRuntime_USDA.SendLog("red", $"Shader '{profile.ShaderName}' not found; skipping material '{matName}'.");
                return null;
            }
            
            Material mat = new Material(shader);
            ApplySurfaceInputs(mat, block, profile);
            ApplyTextures(mat, block, matName, texturesFolder, profile);
            ApplyUvTransform(mat, block, matName, profile.BaseMap);
            return mat;
        }

        /// <summary>Resolves the shader + property names for the active render pipeline (URP / HDRP / Built-in).</summary>
        private static PipelineProfile ResolvePipelineProfile()
        {
            // Defaults: built-in Standard shader.
            PipelineProfile p = new PipelineProfile
            {
                ShaderName = "Standard",
                Color = "_Color",
                BaseMap = "_MainTex",
                Normal = "_BumpMap",
                Height = "_ParallaxMap",
                Metallic = "_Metallic",
                Smoothness = "_Glossiness",
                Emission = "_EmissionColor"
            };
            
            if (GraphicsSettings.currentRenderPipeline == null) return p;
            
            string rpName = GraphicsSettings.currentRenderPipeline.GetType().ToString();
            if (rpName.Contains("Universal"))
            {
                p.ShaderName = "Universal Render Pipeline/Lit";
                p.Color = "_BaseColor";
                p.BaseMap = "_BaseMap";
                p.Smoothness = "_Smoothness";
            }
            else if (rpName.Contains("HDRenderPipeline") || rpName.Contains("HighDefinition"))
            {
                p.ShaderName = "HDRP/Lit";
                p.Color = "_BaseColor";
                p.BaseMap = "_BaseColorMap";
                p.Normal = "_NormalMap";
                p.Height = "_HeightMap";
                p.Smoothness = "_Smoothness";
            }
            
            return p;
        }

        /// <summary>
        /// Applies the <c>UsdPreviewSurface</c> scalar/color inputs (diffuse, metallic, roughness,
        /// emissive) that are explicitly authored in the block.
        /// </summary>
        private static void ApplySurfaceInputs(Material mat, string block, PipelineProfile p)
        {
            Color? diffuse = TryGetColor(block, "diffuseColor");
            if (diffuse.HasValue) mat.SetColor(p.Color, diffuse.Value);
            
            float? metallic = TryGetFloat(block, "metallic");
            if (metallic.HasValue) mat.SetFloat(p.Metallic, Mathf.Clamp01(metallic.Value));
            
            // USD authors roughness; Unity Lit shaders expose smoothness (its inverse).
            float? roughness = TryGetFloat(block, "roughness");
            if (roughness.HasValue) mat.SetFloat(p.Smoothness, Mathf.Clamp01(1f - roughness.Value));
            
            Color? emissive = TryGetColor(block, "emissiveColor");
            if (emissive.HasValue && emissive.Value.maxColorComponent > 0f)
            {
                mat.SetColor(p.Emission, emissive.Value);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
        }

        /// <summary>
        /// Assigns the base / normal / height maps (filename convention <c>_complete/_normal/_height</c>)
        /// and enables the matching shader keywords.
        /// </summary>
        private static void ApplyTextures(Material mat, string block, string matName, string texturesFolder, PipelineProfile p)
        {
            TextureWrapMode? wrap = ParseWrap(TryGetToken(block, "wrapS"));
            bool? sRGB = ParseColorSpace(TryGetToken(block, "sourceColorSpace"));
            
            TryAssignTexture(mat, p.BaseMap, Path.Combine(texturesFolder, matName + "_complete"), false, wrap, sRGB);
            TryAssignTexture(mat, p.Normal, Path.Combine(texturesFolder, matName + "_normal"), true, wrap, null);
            TryAssignTexture(mat, p.Height, Path.Combine(texturesFolder, matName + "_height"), false, wrap, false);
            
            if (mat.GetTexture(p.Normal)) mat.EnableKeyword("_NORMALMAP");
            if (mat.GetTexture(p.Height)) mat.EnableKeyword("_PARALLAXMAP");
        }

        /// <summary>
        /// Applies the <c>UsdTransform2d</c> UV tiling / offset / rotation to the base map. In URP Lit
        /// every map samples with <c>_BaseMap_ST</c>, so transforming the base map transforms them all.
        /// </summary>
        private static void ApplyUvTransform(Material mat, string block, string matName, string baseMapProp)
        {
            Vector2 scale = TryGetVector2(block, "scale") ?? Vector2.one;
            Vector2 offset = TryGetVector2(block, "translation") ?? Vector2.zero;
            mat.SetTextureScale(baseMapProp, scale);
            mat.SetTextureOffset(baseMapProp, offset);
            
            float? rotation = TryGetFloat(block, "rotation");
            if (rotation.HasValue && Mathf.Abs(rotation.Value) > 0.0001f)
                EditorRuntime_USDA.SendLog("orange", $"Material '{matName}': UV rotation ({rotation.Value}) is not applied (URP/Standard Lit has no built-in UV rotation).");
        }

        #endregion



        //=============================================================================
        // USD VALUE PARSING
        //=============================================================================

        #region USD VALUE PARSING

        /// <summary>Reads <c>inputs:&lt;name&gt; = (r, g, b[, a])</c>; ignores ".connect" (textured) inputs.</summary>
        /// <returns>The color, or null when the input is absent or not an inline tuple.</returns>
        private static Color? TryGetColor(string block, string inputName)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(inputName) + @"\s*=\s*\(([^)]+)\)");
            if (!m.Success) return null;
            string[] c = m.Groups[1].Value.Split(',');
            if (c.Length < 3) return null;
            return new Color(ParseF(c[0]), ParseF(c[1]), ParseF(c[2]), c.Length >= 4 ? ParseF(c[3]) : 1f);
        }

        /// <summary>Reads <c>inputs:&lt;name&gt; = (x, y)</c>.</summary>
        /// <returns>The vector, or null when the input is absent.</returns>
        private static Vector2? TryGetVector2(string block, string inputName)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(inputName) + @"\s*=\s*\(([^)]+)\)");
            if (!m.Success) return null;
            string[] c = m.Groups[1].Value.Split(',');
            if (c.Length < 2) return null;
            return new Vector2(ParseF(c[0]), ParseF(c[1]));
        }

        /// <summary>Reads a single scalar <c>inputs:&lt;name&gt; = value</c> (not a tuple, not ".connect").</summary>
        /// <returns>The value, or null when the input is absent.</returns>
        private static float? TryGetFloat(string block, string inputName)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(inputName) + @"\s*=\s*(-?[\d.]+(?:[eE][+-]?\d+)?)");
            if (!m.Success) return null;
            return ParseF(m.Groups[1].Value);
        }

        /// <summary>Reads <c>inputs:&lt;name&gt; = "value"</c>.</summary>
        /// <returns>The token text, or null when the input is absent.</returns>
        private static string TryGetToken(string block, string inputName)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(inputName) + @"\s*=\s*""([^""]+)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Parses a float with invariant culture (trims surrounding whitespace).</summary>
        private static float ParseF(string s)
        {
            return float.Parse(s.Trim(), CultureInfo.InvariantCulture);
        }

        /// <summary>Maps a USD wrap token to a Unity <see cref="TextureWrapMode"/> (null = leave importer default).</summary>
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

        /// <summary>Maps a USD sourceColorSpace token to an sRGB flag (null = leave importer default).</summary>
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

        #endregion

        //=============================================================================
        // TEXTURE IMPORT
        //=============================================================================

        #region TEXTURE IMPORT

        /// <summary>
        /// Finds the texture file for <paramref name="basePath"/> (trying common extensions), applies
        /// its import settings, and assigns it to <paramref name="propName"/> on the material.
        /// </summary>
        private static void TryAssignTexture(Material mat, string propName, string basePath, bool isNormalMap, TextureWrapMode? wrapMode = null, bool? sRGB = null)
        {
            string[] extensions = { ".png", ".jpg", ".tga", ".jpeg" };
            foreach (var ext in extensions)
            {
                string fullPath = (basePath + ext).Replace('\\', '/');
                if (!File.Exists(fullPath)) continue;
                
                ConfigureTextureImport(fullPath, isNormalMap, wrapMode, sRGB);
                
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(fullPath);
                if (tex != null)
                {
                    mat.SetTexture(propName, tex);
                    return;
                }
            }
        }

        /// <summary>
        /// Applies the import settings implied by the USD material (normal-map type, wrap mode, color
        /// space) and reimports the texture only when something actually changed.
        /// </summary>
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
            
            // sRGB only matters for color textures; normal maps are handled by the NormalMap type.
            if (!isNormalMap && sRGB.HasValue && importer.sRGBTexture != sRGB.Value)
            {
                importer.sRGBTexture = sRGB.Value;
                changed = true;
            }
            
            if (changed) importer.SaveAndReimport();
        }

        #endregion


    }
}