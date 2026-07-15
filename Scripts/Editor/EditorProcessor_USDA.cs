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
                
                Material mat = BuildMaterial(block, matName, usdaFolder);
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
        private static Material BuildMaterial(string block, string matName, string usdaFolder)
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
            ApplyTextures(mat, block, usdaFolder, profile);
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
        /// Follows the surface shader's texture connections (diffuse / normal / displacement) and
        /// assigns each declared <c>UsdUVTexture</c> file, with its own wrap mode and color space.
        /// </summary>
        private static void ApplyTextures(Material mat, string block, string usdaFolder, PipelineProfile p)
        {
            AssignConnectedTexture(mat, block, "diffuseColor", p.BaseMap, usdaFolder, false);
            bool hasNormal = AssignConnectedTexture(mat, block, "normal", p.Normal, usdaFolder, true);
            bool hasHeight = AssignConnectedTexture(mat, block, "displacement", p.Height, usdaFolder, false);
            
            if (hasNormal) mat.EnableKeyword("_NORMALMAP");
            if (hasHeight) mat.EnableKeyword("_PARALLAXMAP");
        }

        /// <summary>
        /// Applies the <c>UsdTransform2d</c> UV tiling / offset / rotation to the base map. In URP Lit
        /// every map samples with <c>_BaseMap_ST</c>, so transforming the base map transforms them all.
        /// </summary>
        private static void ApplyUvTransform(Material mat, string block, string matName, string baseMapProp)
        {
            string scope = GetShaderScopeById(block, "UsdTransform2d") ?? block;
            
            Vector2 scale = TryGetVector2(scope, "scale") ?? Vector2.one;
            Vector2 offset = TryGetVector2(scope, "translation") ?? Vector2.zero;
            mat.SetTextureScale(baseMapProp, scale);
            mat.SetTextureOffset(baseMapProp, offset);
            
            float? rotation = TryGetFloat(scope, "rotation");
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
        /// Follows a surface input's <c>.connect</c> to its <c>UsdUVTexture</c> shader, resolves the
        /// declared file (relative to the USDA folder), applies its import settings and assigns it.
        /// </summary>
        /// <returns>True if a texture was found and assigned.</returns>
        private static bool AssignConnectedTexture(Material mat, string block, string surfaceInput, string propName, string usdaFolder, bool isNormalMap)
        {
            string shaderName = GetConnectedShaderName(block, surfaceInput);
            if (shaderName == null) return false;
            
            string scope = GetShaderScope(block, shaderName);
            if (scope == null) return false;
            
            Match fileM = Regex.Match(scope, @"asset inputs:file\s*=\s*@([^@]+)@");
            if (!fileM.Success) return false;
            
            string fullPath = Path.Combine(usdaFolder, fileM.Groups[1].Value.Trim()).Replace('\\', '/');
            if (!File.Exists(fullPath)) return false;
            
            TextureWrapMode? wrap = ParseWrap(TryGetToken(scope, "wrapS"));
            bool? sRGB = isNormalMap ? (bool?)null : ParseColorSpace(TryGetToken(scope, "sourceColorSpace"));
            
            ConfigureTextureImport(fullPath, isNormalMap, wrap, sRGB);
            
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(fullPath);
            if (tex == null) return false;
            
            mat.SetTexture(propName, tex);
            return true;
        }

        /// <summary>
        /// Extracts the shader name a surface input connects to
        /// </summary>
        private static string GetConnectedShaderName(string block, string surfaceInput)
        {
            Match m = Regex.Match(block, @"inputs:" + Regex.Escape(surfaceInput) + @"\.connect\s*=\s*<[^>]*/([^/>.]+)\.outputs");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Returns the text of the <c>def Shader "shaderName"</c> block within a material block.
        /// </summary>
        private static string GetShaderScope(string block, string shaderName)
        {
            string marker = "def Shader \"" + shaderName + "\"";
            int start = block.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            int next = block.IndexOf("def Shader", start + marker.Length, StringComparison.Ordinal);
            return next < 0 ? block.Substring(start) : block.Substring(start, next - start);
        }

        /// <summary>
        /// Returns the text of the first shader block whose <c>info:id</c> equals <paramref name="usdId"/>.
        /// </summary>
        private static string GetShaderScopeById(string block, string usdId)
        {
            string idMarker = "info:id = \"" + usdId + "\"";
            int search = 0;
            while (true)
            {
                int start = block.IndexOf("def Shader", search, StringComparison.Ordinal);
                if (start < 0) return null;
                
                int next = block.IndexOf("def Shader", start + "def Shader".Length, StringComparison.Ordinal);
                string scope = next < 0 ? block.Substring(start) : block.Substring(start, next - start);
                if (scope.Contains(idMarker)) return scope;
                
                if (next < 0) return null;
                search = next;
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