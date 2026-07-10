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
        private Dictionary<GameObject, string> _instanceMeshPath = new ();
        private Dictionary<GameObject, string> _singleBindings = new ();
        private Dictionary<GameObject, Dictionary<string, string>> _subsetBindings = new ();

        #endregion



        //=============================================================================
        // IMPORT ENTRY
        //=============================================================================

        #region IMPORT ENTRY

        /// <summary>
        /// Entry point called by Unity. Ignores non-Cygon files, then dispatches to the single-mesh
        /// or scene importer depending on whether the file contains raw geometry.
        /// </summary>
        public override void OnImportAsset(AssetImportContext ctx)
        {
            // Only handle Cygon files; anything else falls back to Unity's default USD importer.
            if (!CygonUsda.IsCygonFile(assetPath)) return;
            
            ClearCaches();
            
            string rawText = File.ReadAllText(ctx.assetPath);
            string fileName = Path.GetFileNameWithoutExtension(ctx.assetPath);
            
            // A mesh file has raw geometry; a scene file only references meshes + materials.
            if (rawText.Contains("point3f[] points"))
                ImportAsSingleMesh(ctx, rawText, fileName);
            else
                ImportAsScene(ctx, rawText, fileName);
        }

        /// <summary>Resets all per-import caches so a reimport never reuses stale state.</summary>
        private void ClearCaches()
        {
            _meshCache.Clear();
            _meshSubsets.Clear();
            _instanceMeshPath.Clear();
            _singleBindings.Clear();
            _subsetBindings.Clear();
        }

        /// <summary>Imports a standalone mesh USDA as a single GameObject with a mesh + renderer.</summary>
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
        /// <returns>The root container GameObject for the imported scene.</returns>
        private GameObject BuildSceneHierarchy(AssetImportContext ctx, string[] lines, string name)
        {
            GameObject rootContainer = new GameObject(name);
            Stack<GameObject> parentStack = new Stack<GameObject>();
            parentStack.Push(rootContainer);
            
            HashSet<int> finalizedTransforms = new HashSet<int>();
            GameObject activeTarget = null;
            
            // Material-binding context, tracked independently of the hierarchy stack because
            // subset bindings live in nested "over" blocks whose closing brace resets activeTarget.
            GameObject lastMeshInstance = null;
            string currentSubset = null;
            
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                
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
                    continue;
                }
                
                if (trimmed.Contains("{")) { if (activeTarget != null) parentStack.Push(activeTarget); continue; }
                if (trimmed.Contains("}")) { if (parentStack.Count > 1) parentStack.Pop(); activeTarget = null; currentSubset = null; continue; }
                
                if (trimmed.Contains("rel material:binding"))
                {
                    CaptureMaterialBinding(trimmed, lastMeshInstance, currentSubset);
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

        /// <summary>False for Material/Shader prims, which must not become hierarchy GameObjects.</summary>
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
        private static GameObject CreateChild(string objName, Stack<GameObject> parentStack)
        {
            GameObject go = new GameObject(objName);
            go.transform.SetParent(parentStack.Peek().transform);
            return go;
        }

        /// <summary>
        /// Resolves the subset targeted by an "over" line, matching it against the known GeomSubsets
        /// of the instance's mesh.
        /// </summary>
        /// <returns>The subset name, or null (e.g. the mesh-name wrapper "over" around the subsets).</returns>
        private string ResolveSubsetName(string trimmed, GameObject instance)
        {
            Match om = Regex.Match(trimmed, "\"([^\"]+)\"");
            string overName = om.Success ? om.Groups[1].Value : null;
            
            if (overName != null && instance != null &&
                _instanceMeshPath.TryGetValue(instance, out string meshPath) &&
                _meshSubsets.TryGetValue(meshPath, out List<string> subsets) &&
                subsets != null && subsets.Contains(overName))
            {
                return overName;
            }
            
            return null;
        }

        /// <summary>Applies one <c>xformOp</c> line (translate/scale/rotate) to the target transform.</summary>
        /// <returns>True when the line was a transform op and was applied.</returns>
        private bool ApplyTransformProperty(GameObject target, string trimmed, HashSet<int> finalized)
        {
            int objID = target.GetInstanceID();
            
            if (trimmed.Contains("xformOp:translate") && !finalized.Contains(objID + 1))
            {
                target.transform.localPosition = ParseVector3FromLine(trimmed, true);
                finalized.Add(objID + 1);
                return true;
            }
            
            if (trimmed.Contains("xformOp:scale") && !finalized.Contains(objID + 2))
            {
                Vector3 sc = ParseVector3FromLine(trimmed, false);
                if (target.transform.parent != null && target.transform.parent.name == "World") sc = Vector3.one;
                target.transform.localScale = sc;
                finalized.Add(objID + 2);
                return true;
            }
            
            if (trimmed.Contains("xformOp:rotateZYX") && !finalized.Contains(objID + 3))
            {
                target.transform.localEulerAngles = ParseRotationFromLine(trimmed);
                finalized.Add(objID + 3);
                return true;
            }
            
            return false;
        }

        /// <summary>Records a material binding, either per-subset or as the instance's single binding.</summary>
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
        private void ApplyMaterialsToInstances(AssetImportContext ctx)
        {
            foreach (KeyValuePair<GameObject, string> kv in _instanceMeshPath)
            {
                GameObject inst = kv.Key;
                string meshPath = kv.Value;
                if (inst == null) continue;
                
                MeshRenderer renderer = inst.GetComponent<MeshRenderer>();
                if (renderer == null) renderer = inst.AddComponent<MeshRenderer>();
                
                int subMeshCount = 1;
                if (_meshCache.TryGetValue(meshPath, out Mesh mesh) && mesh != null)
                    subMeshCount = Mathf.Max(1, mesh.subMeshCount);
                
                _meshSubsets.TryGetValue(meshPath, out List<string> subsetNames);
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
                    
                    EditorRuntime_USDA.SendLog("green", $"'{inst.name}': {subMeshCount} materials assigned.");
                }
                else
                {
                    Material m = LoadMaterial(singleMat, ctx);
                    for (int si = 0; si < subMeshCount; si++) mats[si] = m;
                }
                
                renderer.sharedMaterials = mats;
            }
        }

        /// <summary>Loads a generated material by name from the sibling "materials" folder.</summary>
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

        /// <summary>Parses a rotation <c>(x, y, z)</c> tuple, negating Y/Z to convert to Unity's convention.</summary>
        private Vector3 ParseRotationFromLine(string line)
        {
            Match m = Regex.Match(line, @"\(([^)]+)\)");
            if (m.Success)
            {
                string[] p = m.Groups[1].Value.Split(',');
                float x = float.Parse(p[0].Trim(), CultureInfo.InvariantCulture);
                float y = float.Parse(p[1].Trim(), CultureInfo.InvariantCulture);
                float z = float.Parse(p[2].Trim(), CultureInfo.InvariantCulture);
                return new Vector3(x, -y, -z);
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Parses <c>def GeomSubset</c> blocks (elementType=face, familyName=materialBind), filling
        /// <paramref name="outNames"/> / <paramref name="outFaces"/> in declaration (== submesh) order.
        /// </summary>
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
        private static float ParseInv(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        /// <summary>Appends every integer found on the value side of a <c>= [ ... ]</c> line.</summary>
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
                // Remember which mesh this instance uses so materials can be assigned
                // once the whole scene (including per-subset bindings) has been parsed.
                _instanceMeshPath[target] = fullPath;
                
                if (!target.GetComponent<MeshFilter>()) target.AddComponent<MeshFilter>().sharedMesh = mesh;
                
                MeshRenderer mr = target.GetComponent<MeshRenderer>();
                if (mr == null) mr = target.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.TwoSided;
                
                MeshCollider mc = target.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
            }
        }

        /// <summary>Parses a mesh USDA into a welded Unity mesh with one submesh per GeomSubset.</summary>
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