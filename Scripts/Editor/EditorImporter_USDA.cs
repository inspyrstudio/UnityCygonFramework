// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;
using UnityEngine.Rendering;

namespace InspyrStudio.CygonLink
{
    /// <summary>
    /// Scripted importer for Cygon USDA files. Builds meshes (with per-face materials via USD
    /// <c>GeomSubset</c> partitions), reconstructs the scene hierarchy and binds generated materials.
    /// </summary>
    [ScriptedImporter(1, "usda")]
    public class EditorImporter_USDA : ScriptedImporter
    {
        //=============================================================================
        // VARIABLES
        //=============================================================================

        #region VARIABLES

        private Dictionary<string, Mesh> _meshCache = new ();
        private Dictionary<string, List<string>> _meshSubsets = new ();
        private Dictionary<GameObject, Mesh> _instanceMesh = new ();
        private Dictionary<GameObject, List<string>> _instanceSubsets = new ();
        private Dictionary<GameObject, string> _singleBindings = new ();
        private Dictionary<GameObject, Dictionary<string, string>> _subsetBindings = new ();

        #endregion



        //=============================================================================
        // IMPORT ENTRY
        //=============================================================================

        #region IMPORT ENTRY

        /// <summary>
        /// Entry point called by Unity. Ignores non-Cygon files, then dispatches to the single-mesh
        /// or the scene importer.
        /// </summary>
        /// <param name="ctx">The import context Unity provides for the asset being imported.</param>
        public override void OnImportAsset(AssetImportContext ctx)
        {
            // Only handle Cygon files; anything else falls back to Unity's default USD importer.
            if (!CygonUsda.IsCygonFile(assetPath)) return;
            
            ClearCaches();
            
            string rawText = File.ReadAllText(ctx.assetPath);
            string fileName = Path.GetFileNameWithoutExtension(ctx.assetPath);
            
            if (IsStandaloneMeshFile(rawText))
                ImportAsSingleMesh(ctx, rawText, fileName);
            else
                ImportAsScene(ctx, rawText, fileName);
        }

        /// <summary>Resets all per-import caches so a reimport never reuses stale state.</summary>
        private void ClearCaches()
        {
            _meshCache.Clear();
            _meshSubsets.Clear();
            _instanceMesh.Clear();
            _instanceSubsets.Clear();
            _singleBindings.Clear();
            _subsetBindings.Clear();
        }

        /// <summary>
        /// Tells a standalone mesh file (raw geometry only, referenced by a scene) from a scene file.
        /// A scene declares materials, references other files, or holds several meshes; since the
        /// single-file export embeds its meshes, geometry alone no longer identifies a mesh file.
        /// </summary>
        /// <param name="rawText">Full text of the USDA.</param>
        /// <returns>True when the file is a standalone mesh and can be imported as one object.</returns>
        private static bool IsStandaloneMeshFile(string rawText)
        {
            if (!rawText.Contains("point3f[] points")) return false;
            if (rawText.Contains("def Material")) return false;
            if (rawText.Contains("prepend references")) return false;
            return CountOccurrences(rawText, "def Mesh ") <= 1;
        }

        /// <summary>Counts the non-overlapping occurrences of a substring.</summary>
        /// <param name="text">The text to scan.</param>
        /// <param name="token">The substring to count.</param>
        /// <returns>How many times <paramref name="token"/> occurs in <paramref name="text"/>.</returns>
        private static int CountOccurrences(string text, string token)
        {
            int count = 0, i = 0;
            while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) != -1)
            {
                count++;
                i += token.Length;
            }
            return count;
        }

        /// <summary>Imports a standalone mesh USDA as a single GameObject with a mesh + renderer.</summary>
        /// <param name="ctx">The import context to attach the created objects to.</param>
        /// <param name="text">Raw text of the mesh USDA.</param>
        /// <param name="name">Name for the created GameObject.</param>
        private void ImportAsSingleMesh(AssetImportContext ctx, string text, string name)
        {
            Mesh mesh = BuildMeshFromUsda(text, out _);
            if (mesh == null) return;
            
            GameObject go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();
            
            ctx.AddObjectToAsset("main", go);
            ctx.AddObjectToAsset("mesh", mesh);
            ctx.SetMainObject(go);
        }

        /// <summary>Imports a scene USDA: builds the hierarchy, then binds generated materials.</summary>
        /// <param name="ctx">The import context to attach created objects to.</param>
        /// <param name="text">Raw text of the scene USDA.</param>
        /// <param name="name">Name for the root container GameObject.</param>
        private void ImportAsScene(AssetImportContext ctx, string text, string name)
        {
            string[] lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            
            GameObject rootContainer = BuildSceneHierarchy(ctx, lines, name);
            ApplyMaterialsToInstances(ctx);
            
            EditorRuntime_USDA.SendLog("green", $"Scene '{name}': import complete.");
            
            ctx.AddObjectToAsset("main_root", rootContainer);
            ctx.SetMainObject(rootContainer);
        }

        #endregion



        //=============================================================================
        // SCENE HIERARCHY
        //=============================================================================

        #region SCENE HIERARCHY

        /// <summary>
        /// Walks the prim tree, spawning GameObjects, applying transforms and recording
        /// material bindings.
        /// </summary>
        /// <param name="ctx">The import context (used to attach referenced meshes).</param>
        /// <param name="lines">The scene USDA split into lines.</param>
        /// <param name="name">Name for the root container GameObject (the asset file name).</param>
        /// <returns>The root container GameObject for the imported scene.</returns>
        private GameObject BuildSceneHierarchy(AssetImportContext ctx, string[] lines, string name)
        {
            GameObject rootContainer = new GameObject(name);
            Stack<GameObject> parentStack = new Stack<GameObject>();
            parentStack.Push(rootContainer);
            
            HashSet<(int, TransformOp)> finalizedTransforms = new HashSet<(int, TransformOp)>();
            GameObject activeTarget = null;
            
            // Material-binding context, tracked independently of the hierarchy stack because
            // subset bindings live in nested "over" blocks whose closing brace resets activeTarget.
            GameObject lastMeshInstance = null;
            string currentSubset = null;
            
            for (int li = 0; li < lines.Length; li++)
            {
                string trimmed = lines[li].Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                // Inline mesh (single-file export): build it from its own block and attach it to the
                // enclosing prim, then skip the block so its geometry is not read as scene properties
                // (and so its braces cannot unbalance the hierarchy stack).
                if (trimmed.StartsWith("def Mesh "))
                {
                    int blockEnd = FindBlockEnd(lines, li);
                    GameObject owner = parentStack.Peek();
                    if (owner != rootContainer)
                    {
                        ApplyInlineMesh(ctx, owner, lines, li, blockEnd);
                        lastMeshInstance = owner;
                    }
                    li = blockEnd;
                    activeTarget = null;
                    continue;
                }

                // Definition: spawn a GameObject (unless it's a material/shader prim).
                if (trimmed.StartsWith("def "))
                {
                    int fQ = trimmed.IndexOf('"'), lQ = trimmed.LastIndexOf('"');
                    if (fQ != -1 && lQ > fQ)
                    {
                        string objName = trimmed.Substring(fQ + 1, lQ - fQ - 1);
                        activeTarget = IsHierarchyObject(objName, trimmed) ? CreateChild(objName, parentStack) : null;
                        continue;
                    }
                }
                
                // Subset re-binding block: "over" whose name matches a GeomSubset of the current mesh.
                if (trimmed.StartsWith("over "))
                {
                    currentSubset = ResolveSubsetName(trimmed, lastMeshInstance);
                    activeTarget = null;
                    continue;
                }
                
                if (trimmed.Contains("{")) { parentStack.Push(activeTarget != null ? activeTarget : parentStack.Peek()); continue; }
                if (trimmed.Contains("}")) { if (parentStack.Count > 1) parentStack.Pop(); activeTarget = null; currentSubset = null; continue; }
                
                if (trimmed.Contains("rel material:binding"))
                {
                    // A subset binding belongs to the mesh being overridden, so it targets the last mesh
                    // instance. A prim-level binding belongs to the prim whose block we are inside, which
                    // is the stack top: in the single-file export it is declared before the inline mesh,
                    // so lastMeshInstance is not set yet and would point at the previous object.
                    GameObject bindingTarget = currentSubset != null ? lastMeshInstance : parentStack.Peek();
                    if (bindingTarget != rootContainer) CaptureMaterialBinding(trimmed, bindingTarget, currentSubset);
                    continue;
                }
                
                // Per-object properties (transforms + mesh reference).
                if (activeTarget != null && activeTarget != rootContainer)
                {
                    if (ApplyTransformProperty(activeTarget, trimmed, finalizedTransforms)) continue;
                    
                    if (trimmed.Contains("prepend references"))
                    {
                        Match refM = Regex.Match(trimmed, @"@([^@]+)@");
                        if (refM.Success)
                        {
                            ApplyMeshReference(ctx, activeTarget, refM.Groups[1].Value);
                            lastMeshInstance = activeTarget;
                        }
                    }
                }
            }
            
            return rootContainer;
        }

        /// <summary>Decides whether a <c>def</c> prim should become a hierarchy GameObject.</summary>
        /// <param name="objName">The prim's name.</param>
        /// <param name="trimmed">The trimmed <c>def</c> line (checked for material/shader keywords).</param>
        /// <returns>False for Material/Shader prims, true for regular hierarchy objects.</returns>
        private static bool IsHierarchyObject(string objName, string trimmed)
        {
            return !(objName == "Materials"
                || objName == "PBRShader"
                || trimmed.Contains("Material")
                || trimmed.Contains("Shader")
                || trimmed.Contains("stReader")
                || trimmed.Contains("texture")
                || trimmed.Contains("transform2d"));
        }

        /// <summary>Creates a GameObject parented under the current top of the hierarchy stack.</summary>
        /// <param name="objName">Name of the GameObject to create.</param>
        /// <param name="parentStack">Hierarchy stack whose top becomes the new object's parent.</param>
        /// <returns>The newly created child GameObject.</returns>
        private static GameObject CreateChild(string objName, Stack<GameObject> parentStack)
        {
            GameObject go = new GameObject(objName);
            go.transform.SetParent(parentStack.Peek().transform);
            return go;
        }

        /// <summary>Finds the line that closes the block opened at or after <paramref name="start"/></summary>
        /// <param name="lines">All lines of the file being parsed</param>
        /// <param name="start">Index of the prim declaration line</param>
        /// <returns>Index of the matching closing brace, or the last line when the block is unterminated</returns>
        private static int FindBlockEnd(string[] lines, int start)
        {
            int depth = 0;
            bool opened = false;
            
            for (int i = start; i < lines.Length; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == '{') { depth++; opened = true; }
                    else if (c == '}') depth--;
                }
                
                if (opened && depth <= 0) return i;
            }
            
            return lines.Length - 1;
        }

        /// <summary>
        /// Builds a mesh from an inline <c>def Mesh</c> block (single-file export) and attaches it to the
        /// prim enclosing it, along with the material bindings its GeomSubsets declare
        /// </summary>
        /// <param name="ctx">The import context (registers the mesh as a sub-asset)</param>
        /// <param name="owner">The enclosing prim's GameObject, which receives the mesh</param>
        /// <param name="lines">All lines of the scene USDA</param>
        /// <param name="start">Index of the <c>def Mesh</c> line</param>
        /// <param name="end">Index of the line closing the mesh block</param>
        private void ApplyInlineMesh(AssetImportContext ctx, GameObject owner, string[] lines, int start, int end)
        {
            string block = string.Join("\n", lines, start, end - start + 1);
            
            Mesh mesh = BuildMeshFromUsda(block, out List<string> subsetNames);
            if (mesh == null) return;
            
            ApplyMeshPrimOffset(mesh, lines, start, end, owner.name);
            
            mesh.name = owner.name;
            ctx.AddObjectToAsset(owner.name + "_m", mesh);
            
            AttachMesh(owner, mesh);
            _instanceMesh[owner] = mesh;
            _instanceSubsets[owner] = subsetNames;
            
            CaptureSubsetBindings(block, owner);
        }

        /// <summary>
        /// Bakes the mesh prim's own transform into its vertices. Only a translation is baked: the
        /// exporter uses this op purely to offset geometry from its pivot, and a rotation or scale
        /// there is reported rather than silently dropped.
        /// </summary>
        /// <param name="mesh">The freshly built mesh, modified in place</param>
        /// <param name="lines">All lines of the scene USDA</param>
        /// <param name="start">Index of the <c>def Mesh</c> line</param>
        /// <param name="end">Index of the line closing the mesh block</param>
        /// <param name="ownerName">Name of the enclosing prim, used for log message</param>
        private void ApplyMeshPrimOffset(Mesh mesh, string[] lines, int start, int end, string ownerName)
        {
            Vector3 offset = Vector3.zero;
            
            for (int i = start + 1; i <= end; i++)
            {
                string trimmed = lines[i].Trim();
                
                // Only the mesh prim's own ops count; stop at its first nested prim (a GeomSubset).
                if (trimmed.StartsWith("def ")) break;
                if (trimmed.Contains("xformOpOrder")) continue;
                
                if (trimmed.Contains("xformOp:translate"))
                {
                    offset = ParseVector3FromLine(trimmed, true);
                }
                else if (trimmed.Contains("xformOp:rotate") && ParseRotationFromLine(trimmed) != Vector3.zero)
                {
                    EditorRuntime_USDA.SendLog("orange", $"'{ownerName}': rotation on the mesh prim is not applied.");
                }
                else if (trimmed.Contains("xformOp:scale") && ParseVector3FromLine(trimmed, false) != Vector3.one)
                {
                    EditorRuntime_USDA.SendLog("orange", $"'{ownerName}': scale on the mesh prim is not applied.");
                }
            }
            
            if (offset == Vector3.zero) return;
            
            Vector3[] verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++) verts[i] += offset;
            
            mesh.vertices = verts;
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Records the material bound by each <c>def GeomSubset</c> of an inline mesh block. In the
        /// single-file export the binding sits in the subset prim itself rather than in an "over" block
        /// </summary>
        /// <param name="block">Text of the <c>def Mesh</c> block</param>
        /// <param name="owner">The GameObject carrying the mesh</param>
        private void CaptureSubsetBindings(string block, GameObject owner)
        {
            string subset = null;
            
            foreach (string raw in block.Split('\n'))
            {
                string trimmed = raw.Trim();
                
                if (trimmed.StartsWith("def GeomSubset"))
                {
                    Match m = Regex.Match(trimmed, "\"([^\"]+)\"");
                    subset = m.Success ? m.Groups[1].Value : null;
                }
                else if (subset != null && trimmed.Contains("rel material:binding"))
                {
                    CaptureMaterialBinding(trimmed, owner, subset);
                    subset = null;
                }
            }
        }

        /// <summary>Wires a mesh onto a GameObject with a renderer and a matching collider</summary>
        /// <param name="target">The GameObject that receives the components</param>
        /// <param name="mesh">The mesh to attach</param>
        private static void AttachMesh(GameObject target, Mesh mesh)
        {
            if (!target.GetComponent<MeshFilter>()) target.AddComponent<MeshFilter>().sharedMesh = mesh;
            
            MeshRenderer mr = target.GetComponent<MeshRenderer>();
            if (mr == null) mr = target.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.TwoSided;
            
            MeshCollider mc = target.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
        }

        /// <summary>
        /// Resolves the subset targeted by an "over" line, matching it against the known GeomSubsets
        /// of the instance's mesh.
        /// </summary>
        /// <param name="trimmed">The trimmed <c>over</c> line.</param>
        /// <param name="instance">The mesh instance the <c>over</c> block applies to.</param>
        /// <returns>The subset name, or null (e.g. the mesh-name wrapper "over" around the subsets).</returns>
        private string ResolveSubsetName(string trimmed, GameObject instance)
        {
            Match om = Regex.Match(trimmed, "\"([^\"]+)\"");
            string overName = om.Success ? om.Groups[1].Value : null;
            
            if (overName != null && instance != null &&
                _instanceSubsets.TryGetValue(instance, out List<string> subsets) &&
                subsets != null && subsets.Contains(overName))
            {
                return overName;
            }
            
            return null;
        }

        /// <summary>The xform operations tracked per object, so only the first declaration wins.</summary>
        private enum TransformOp { Translate, Scale, Rotate }

        /// <summary>Applies one <c>xformOp</c> line (translate/scale/rotate) to the target transform.</summary>
        /// <param name="target">The GameObject whose transform is set.</param>
        /// <param name="trimmed">The trimmed property line to interpret.</param>
        /// <param name="finalized">(object, op) pairs already applied, so a later line cannot overwrite them.</param>
        /// <returns>True when the line was a transform op and was applied.</returns>
        private bool ApplyTransformProperty(GameObject target, string trimmed, HashSet<(int, TransformOp)> finalized)
        {
            if (trimmed.Contains("xformOpOrder")) return false;
            
            int objID = target.GetInstanceID();
            
            if (trimmed.Contains("xformOp:translate") && finalized.Add((objID, TransformOp.Translate)))
            {
                target.transform.localPosition = ParseVector3FromLine(trimmed, true);
                return true;
            }
            
            if (trimmed.Contains("xformOp:scale") && finalized.Add((objID, TransformOp.Scale)))
            {
                Vector3 sc = ParseVector3FromLine(trimmed, false);
                if (target.transform.parent != null && target.transform.parent.name == "World") sc = Vector3.one;
                target.transform.localScale = sc;
                return true;
            }
            
            // Matches any rotate variant
            if (trimmed.Contains("xformOp:rotate") && finalized.Add((objID, TransformOp.Rotate)))
            {
                target.transform.localEulerAngles = ParseRotationFromLine(trimmed);
                return true;
            }
            
            return false;
        }

        /// <summary>Records a material binding, either per-subset or as the instance's single binding.</summary>
        /// <param name="trimmed">The trimmed <c>rel material:binding</c> line.</param>
        /// <param name="instance">The mesh instance the binding belongs to.</param>
        /// <param name="subset">Subset name for a per-face binding, or null for the instance-wide binding.</param>
        private void CaptureMaterialBinding(string trimmed, GameObject instance, string subset)
        {
            int lastSlash = trimmed.LastIndexOf('/');
            int closeBracket = trimmed.LastIndexOf('>');
            if (lastSlash == -1 || closeBracket <= lastSlash || instance == null) return;
            
            string matName = trimmed.Substring(lastSlash + 1, closeBracket - lastSlash - 1);
            
            if (subset != null)
            {
                if (!_subsetBindings.TryGetValue(instance, out Dictionary<string, string> binds))
                {
                    binds = new Dictionary<string, string>();
                    _subsetBindings[instance] = binds;
                }
                binds[subset] = matName;
            }
            else
            {
                _singleBindings[instance] = matName;
            }
        }

        #endregion



        //=============================================================================
        // MATERIAL BINDING
        //=============================================================================

        #region MATERIAL BINDING

        /// <summary>
        /// Builds the <c>sharedMaterials</c> array (one slot per submesh) for every mesh instance,
        /// matching each GeomSubset to the material bound to it.
        /// </summary>
        /// <param name="ctx">The import context (used to load and depend on the .mat assets).</param>
        private void ApplyMaterialsToInstances(AssetImportContext ctx)
        {
            int multiMaterial = 0;
            
            foreach (KeyValuePair<GameObject, Mesh> kv in _instanceMesh)
            {
                GameObject inst = kv.Key;
                Mesh mesh = kv.Value;
                if (inst == null) continue;
                
                MeshRenderer renderer = inst.GetComponent<MeshRenderer>();
                if (renderer == null) renderer = inst.AddComponent<MeshRenderer>();
                
                int subMeshCount = mesh != null ? Mathf.Max(1, mesh.subMeshCount) : 1;
                
                _instanceSubsets.TryGetValue(inst, out List<string> subsetNames);
                _subsetBindings.TryGetValue(inst, out Dictionary<string, string> subBinds);
                _singleBindings.TryGetValue(inst, out string singleMat);
                
                Material[] mats = new Material[subMeshCount];
                bool hasSubsets = subsetNames != null && subsetNames.Count > 0;
                
                if (hasSubsets)
                {
                    for (int si = 0; si < subMeshCount; si++)
                    {
                        string matName = null;
                        if (si < subsetNames.Count && subBinds != null)
                            subBinds.TryGetValue(subsetNames[si], out matName);
                        
                        // Fallback for a submesh with no explicit binding (e.g. leftover faces).
                        if (string.IsNullOrEmpty(matName)) matName = singleMat;
                        mats[si] = LoadMaterial(matName, ctx);
                    }
                    
                    multiMaterial++;
                }
                else
                {
                    Material m = LoadMaterial(singleMat, ctx);
                    for (int si = 0; si < subMeshCount; si++) mats[si] = m;
                }
                
                renderer.sharedMaterials = mats;
            }
            
            EditorRuntime_USDA.SendLog("green", $"{_instanceMesh.Count} meshes bound ({multiMaterial} with per-face materials).");
        }

        /// <summary>Loads a generated material by name from the sibling "materials" folder.</summary>
        /// <param name="matName">Material name without extension (a <c>.mat</c> under "materials/").</param>
        /// <param name="ctx">The import context (registers a dependency on the loaded .mat).</param>
        /// <returns>The material, or null when the name is empty or the asset is missing.</returns>
        private Material LoadMaterial(string matName, AssetImportContext ctx)
        {
            if (string.IsNullOrEmpty(matName)) return null;
            
            string materialPath = Path.Combine(Path.GetDirectoryName(ctx.assetPath), "materials", matName + ".mat").Replace('\\', '/');
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat != null) ctx.DependsOnSourceAsset(materialPath);
            return mat;
        }

        #endregion



        //=============================================================================
        // USD VALUE PARSING
        //=============================================================================

        #region USD VALUE PARSING

        /// <summary>Parses a <c>(x, y, z)</c> tuple into a Vector3, optionally flipping Z for handedness.</summary>
        /// <param name="line">The line containing a <c>(x, y, z)</c> tuple.</param>
        /// <param name="flipZ">True to negate Z (USD → Unity handedness conversion).</param>
        /// <returns>The parsed vector; zero (flipZ) or one otherwise when no tuple is found.</returns>
        private Vector3 ParseVector3FromLine(string line, bool flipZ)
        {
            Match m = Regex.Match(line, @"\(([^)]+)\)");
            if (m.Success)
            {
                string[] p = m.Groups[1].Value.Split(',');
                float x = float.Parse(p[0].Trim(), CultureInfo.InvariantCulture);
                float y = float.Parse(p[1].Trim(), CultureInfo.InvariantCulture);
                float z = float.Parse(p[2].Trim(), CultureInfo.InvariantCulture);
                return new Vector3(x, y, flipZ ? -z : z);
            }
            return flipZ ? Vector3.zero : Vector3.one;
        }

        /// <summary>Parses a rotation <c>(x, y, z)</c> tuple and converts it to Unity's handedness.</summary>
        /// <param name="line">The line containing a <c>(x, y, z)</c> rotation tuple.</param>
        /// <returns>The Euler angles in Unity's convention, or zero when no tuple is found.</returns>
        private Vector3 ParseRotationFromLine(string line)
        {
            Match m = Regex.Match(line, @"\(([^)]+)\)");
            if (m.Success)
            {
                string[] p = m.Groups[1].Value.Split(',');
                float x = float.Parse(p[0].Trim(), CultureInfo.InvariantCulture);
                float y = float.Parse(p[1].Trim(), CultureInfo.InvariantCulture);
                float z = float.Parse(p[2].Trim(), CultureInfo.InvariantCulture);
                
                return new Vector3(-x, -y, z);
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Parses <c>def GeomSubset</c> blocks (elementType=face, familyName=materialBind), filling
        /// <paramref name="outNames"/> / <paramref name="outFaces"/> in declaration (== submesh) order.
        /// </summary>
        /// <param name="lines">The mesh USDA split into lines.</param>
        /// <param name="outNames">Receives each material-bind subset's name, in declaration order.</param>
        /// <param name="outFaces">Receives each subset's face indices, parallel to <paramref name="outNames"/>.</param>
        private void ParseGeomSubsets(string[] lines, List<string> outNames, List<List<int>> outFaces)
        {
            string curName = null;
            List<int> curFaces = null;
            bool isMaterialBind = false;
            bool isFace = false;
            bool inSubset = false;
            
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                
                if (trimmed.StartsWith("def GeomSubset"))
                {
                    Match m = Regex.Match(trimmed, "\"([^\"]+)\"");
                    curName = m.Success ? m.Groups[1].Value : null;
                    curFaces = new List<int>();
                    isMaterialBind = false;
                    isFace = false;
                    inSubset = true;
                    continue;
                }
                
                if (!inSubset) continue;
                
                if (trimmed.Contains("familyName") && trimmed.Contains("materialBind")) isMaterialBind = true;
                else if (trimmed.Contains("elementType") && trimmed.Contains("face")) isFace = true;
                else if (trimmed.Contains("int[] indices")) AppendInts(trimmed, curFaces);
                else if (trimmed.StartsWith("}"))
                {
                    if (curName != null && isMaterialBind && isFace && curFaces != null)
                    {
                        outNames.Add(curName);
                        outFaces.Add(curFaces);
                    }
                    inSubset = false;
                    curName = null;
                    curFaces = null;
                }
            }
        }

        /// <summary>Parses a float with invariant culture.</summary>
        /// <param name="s">The string to parse.</param>
        /// <returns>The parsed float value.</returns>
        private static float ParseInv(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        /// <summary>Appends every integer found on the value side of a <c>= [ ... ]</c> line.</summary>
        /// <param name="trimmed">The line containing a <c>= [ ... ]</c> array of integers.</param>
        /// <param name="target">The list the parsed integers are appended to.</param>
        private static void AppendInts(string trimmed, List<int> target)
        {
            string data = trimmed.Contains("=") ? trimmed.Split('=')[1] : trimmed;
            foreach (Match m in Regex.Matches(data, @"\d+")) target.Add(int.Parse(m.Value));
        }

        #endregion



        //=============================================================================
        // MESH BUILDING
        //=============================================================================

        #region MESH BUILDING

        /// <summary>Raw per-corner arrays parsed straight from a mesh USDA (before welding).</summary>
        private class MeshData
        {
            public readonly List<Vector3> Points = new ();
            public readonly List<Vector2> Uvs = new ();
            public readonly List<Vector3> Normals = new ();
            public readonly List<int> FaceIndices = new ();
            public readonly List<int> FaceVertexCounts = new ();
        }

        /// <summary>
        /// Resolves a referenced mesh file (building + caching it on first use) and wires the mesh,
        /// renderer and collider onto the instance GameObject.
        /// </summary>
        /// <param name="ctx">The import context (caches the built mesh as a sub-asset).</param>
        /// <param name="target">The instance GameObject to attach the mesh/renderer/collider to.</param>
        /// <param name="path">USDA-relative path to the referenced mesh file.</param>
        private void ApplyMeshReference(AssetImportContext ctx, GameObject target, string path)
        {
            string fullPath = Path.Combine(Path.GetDirectoryName(ctx.assetPath), path.Trim().Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) return;
            
            if (!_meshCache.ContainsKey(fullPath))
            {
                Mesh m = BuildMeshFromUsda(File.ReadAllText(fullPath), out List<string> subsetNames);
                if (m != null)
                {
                    m.name = Path.GetFileNameWithoutExtension(fullPath);
                    _meshCache[fullPath] = m;
                    _meshSubsets[fullPath] = subsetNames;
                    ctx.AddObjectToAsset(target.name + "_m", m);
                }
            }
            
            if (_meshCache.TryGetValue(fullPath, out Mesh mesh))
            {
                _instanceMesh[target] = mesh;
                _meshSubsets.TryGetValue(fullPath, out List<string> subsetNames);
                _instanceSubsets[target] = subsetNames;
                
                AttachMesh(target, mesh);
            }
        }

        /// <summary>Parses a mesh USDA into a welded Unity mesh with one submesh per GeomSubset.</summary>
        /// <param name="rawText">Raw text of the mesh USDA.</param>
        /// <param name="subsetNames">Receives the GeomSubset names in submesh order (empty if none).</param>
        /// <returns>The built mesh.</returns>
        private Mesh BuildMeshFromUsda(string rawText, out List<string> subsetNames)
        {
            subsetNames = new List<string>();
            string[] lines = rawText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            
            MeshData data = ParseMeshArrays(lines);
            
            List<List<int>> subsetFaces = new List<List<int>>();
            ParseGeomSubsets(lines, subsetNames, subsetFaces);
            
            Mesh mesh = new Mesh { name = "USDA_Mesh_Final_Fix" };
            BuildUnweldedMesh(mesh, data);
            WeldVertices(mesh, subsetFaces, data.FaceVertexCounts);   // welds + splits into submeshes
            RefineNormals(mesh);
            return mesh;
        }

        /// <summary>
        /// Reads points / faceVertexCounts / faceVertexIndices / st / normals into a <see cref="MeshData"/>.
        /// Points and normals get their Z flipped to convert from the USD to Unity handedness.
        /// </summary>
        /// <param name="lines">The mesh USDA split into lines.</param>
        /// <returns>A <see cref="MeshData"/> holding the parsed (Z-flipped) arrays.</returns>
        private static MeshData ParseMeshArrays(string[] lines)
        {
            MeshData data = new MeshData();
            int currentMode = 0;
            
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("point3f[] points")) currentMode = 1;
                else if (trimmed.Contains("int[] faceVertexCounts")) currentMode = 5;
                else if (trimmed.Contains("int[] faceVertexIndices")) currentMode = 2;
                else if (trimmed.Contains("float2[] primvars:st")) currentMode = 3;
                else if (trimmed.Contains("normal3f[] normals")) currentMode = 4;
                
                switch (currentMode)
                {
                    case 1:
                        foreach (Match m in Regex.Matches(trimmed, @"\(([^)]+)\)"))
                        {
                            string[] c = m.Groups[1].Value.Split(',');
                            data.Points.Add(new Vector3(ParseInv(c[0]), ParseInv(c[1]), -ParseInv(c[2])));
                        }
                        break;
                    case 5:
                        AppendInts(trimmed, data.FaceVertexCounts);
                        break;
                    case 2:
                        AppendInts(trimmed, data.FaceIndices);
                        break;
                    case 3:
                        foreach (Match m in Regex.Matches(trimmed, @"\(([^)]+)\)"))
                        {
                            string[] c = m.Groups[1].Value.Split(',');
                            data.Uvs.Add(new Vector2(ParseInv(c[0]), ParseInv(c[1])));
                        }
                        break;
                    case 4:
                        foreach (Match m in Regex.Matches(trimmed, @"\(([^)]+)\)"))
                        {
                            string[] c = m.Groups[1].Value.Split(',');
                            // Flip Z to match the point flipping; normalize for Unity's lighting engine.
                            data.Normals.Add(new Vector3(ParseInv(c[0]), ParseInv(c[1]), -ParseInv(c[2])).normalized);
                        }
                        break;
                }
                
                if (trimmed.Contains("]")) currentMode = 0;
            }
            
            return data;
        }

        /// <summary>Expands the parsed arrays into per-corner (unwelded) vertices/uvs/normals/triangles.</summary>
        /// <param name="mesh">The mesh to populate.</param>
        /// <param name="data">The parsed per-corner arrays to expand.</param>
        private static void BuildUnweldedMesh(Mesh mesh, MeshData data)
        {
            int total = data.FaceIndices.Count;
            Vector3[] verts = new Vector3[total];
            Vector2[] uvs = new Vector2[total];
            Vector3[] normals = new Vector3[total];
            int[] triangles = new int[total];
            
            for (int i = 0; i < total; i++)
            {
                verts[i] = data.Points[data.FaceIndices[i]];
                if (i < data.Uvs.Count) uvs[i] = data.Uvs[i];
                if (i < data.Normals.Count) normals[i] = LiftDownwardNormal(data.Normals[i]);
                triangles[i] = i;
            }
            
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;
        }

        /// <summary>Nudges strongly downward-facing normals slightly up so floors don't render pure black.</summary>
        /// <param name="n">The normal to adjust.</param>
        /// <returns>The normal, nudged upward when it faced strongly downward.</returns>
        private static Vector3 LiftDownwardNormal(Vector3 n)
        {
            n = n.normalized;
            if (n.y < 0.2f)
            {
                n.y = Mathf.Lerp(n.y, 0.2f, 0.5f);
                n = n.normalized;
            }
            return n;
        }

        /// <summary>
        /// Welds duplicate vertices and, when GeomSubsets are provided, splits the triangles into one
        /// submesh per subset (Unity's mechanism for per-face materials).
        /// </summary>
        /// <param name="mesh">The mesh to weld in place (its vertices/normals/uvs are rewritten).</param>
        /// <param name="subsetFaces">Face-index lists per GeomSubset; null or empty produces a single submesh.</param>
        /// <param name="faceVertexCounts">Vertex count per face, used to map faces to triangle ranges.</param>
        private void WeldVertices(Mesh mesh, List<List<int>> subsetFaces, List<int> faceVertexCounts)
        {
            Vector3[] verts = mesh.vertices;
            Vector3[] norms = mesh.normals;
            Vector2[] uvs = mesh.uv;
            
            Dictionary<string, int> duplicateCheck = new Dictionary<string, int>();
            List<int> newTriangles = new List<int>();
            List<Vector3> uniqueVerts = new List<Vector3>();
            List<Vector3> uniqueNorms = new List<Vector3>();
            List<Vector2> uniqueUVs = new List<Vector2>();
            
            for (int i = 0; i < verts.Length; i++)
            {
                // Snap to 3 decimals so float precision errors don't prevent welding.
                string key = string.Format(CultureInfo.InvariantCulture, "{0:F3}{1:F3}{2:F3}{3:F3}{4:F3}{5:F3}{6:F3}{7:F3}",
                    verts[i].x, verts[i].y, verts[i].z,
                    norms[i].x, norms[i].y, norms[i].z,
                    uvs[i].x, uvs[i].y);

                if (!duplicateCheck.TryGetValue(key, out int index))
                {
                    index = uniqueVerts.Count;
                    duplicateCheck.Add(key, index);
                    uniqueVerts.Add(verts[i]);
                    uniqueNorms.Add(norms[i]);
                    uniqueUVs.Add(uvs[i]);
                }
                newTriangles.Add(index);
            }
            
            mesh.Clear();
            mesh.SetVertices(uniqueVerts);
            mesh.SetNormals(uniqueNorms);
            mesh.SetUVs(0, uniqueUVs);
            
            // No subsets -> a single submesh, exactly like before.
            if (subsetFaces == null || subsetFaces.Count == 0)
            {
                mesh.subMeshCount = 1;
                mesh.SetTriangles(newTriangles, 0);
                return;
            }
            
            // newTriangles is in face-corner order, so face f occupies the corner range
            // [faceStart[f], faceStart[f] + count[f]).
            int faceCount = (faceVertexCounts != null && faceVertexCounts.Count > 0)
                ? faceVertexCounts.Count
                : newTriangles.Count / 3;
            
            int[] faceStart = new int[faceCount];
            int acc = 0;
            for (int f = 0; f < faceCount; f++)
            {
                faceStart[f] = acc;
                acc += (faceVertexCounts != null && f < faceVertexCounts.Count) ? faceVertexCounts[f] : 3;
            }
            
            bool[] assigned = new bool[faceCount];
            List<List<int>> submeshTris = new List<List<int>>();
            
            for (int s = 0; s < subsetFaces.Count; s++)
            {
                List<int> tris = new List<int>();
                foreach (int f in subsetFaces[s])
                {
                    if (f < 0 || f >= faceCount) continue;
                    assigned[f] = true;
                    int start = faceStart[f];
                    int cnt = (faceVertexCounts != null && f < faceVertexCounts.Count) ? faceVertexCounts[f] : 3;
                    for (int k = 0; k < cnt; k++)
                    {
                        int idx = start + k;
                        if (idx < newTriangles.Count) tris.Add(newTriangles[idx]);
                    }
                }
                submeshTris.Add(tris);
            }
            
            // Faces not covered by any subset go into an extra submesh so no geometry is lost.
            List<int> leftover = new List<int>();
            for (int f = 0; f < faceCount; f++)
            {
                if (assigned[f]) continue;
                int start = faceStart[f];
                int cnt = (faceVertexCounts != null && f < faceVertexCounts.Count) ? faceVertexCounts[f] : 3;
                for (int k = 0; k < cnt; k++)
                {
                    int idx = start + k;
                    if (idx < newTriangles.Count) leftover.Add(newTriangles[idx]);
                }
            }
            if (leftover.Count > 0) submeshTris.Add(leftover);
            
            mesh.subMeshCount = submeshTris.Count;
            for (int s = 0; s < submeshTris.Count; s++)
                mesh.SetTriangles(submeshTris[s], s);
        }

        /// <summary>
        /// Blends authored USDA normals toward Unity's flat face normals at corners/edges so cracks
        /// catch light, while leaving flat faces on their exact authored normal.
        /// </summary>
        /// <param name="mesh">The mesh whose normals are refined in place.</param>
        private static void RefineNormals(Mesh mesh)
        {
            Vector3[] usdaNormals = mesh.normals;
            
            mesh.RecalculateNormals();
            Vector3[] flatNormals = mesh.normals;
            
            Vector3[] result = new Vector3[usdaNormals.Length];
            for (int i = 0; i < usdaNormals.Length; i++)
            {
                float similarity = Vector3.Dot(usdaNormals[i], flatNormals[i]);
                result[i] = similarity < 0.99f
                    ? Vector3.Lerp(usdaNormals[i], flatNormals[i], 0.6f).normalized
                    : usdaNormals[i];
            }
            
            mesh.normals = result;
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
        }

        #endregion


    }
}