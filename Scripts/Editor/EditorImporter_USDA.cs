// Copyright 2026 Inspyr Studio, SAS. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Linq;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;
using UnityEngine.Rendering;

namespace InspyrStudio.CygonLink
{
    [ScriptedImporter(1,"usda")]
    public class EditorImporter_USDA : ScriptedImporter
    {
         //=============================================================================
        // VARIABLES
        //=============================================================================

        #region VARIABLES

        private Dictionary<string, Mesh> _meshCache = new Dictionary<string, Mesh>();
        private Dictionary<string, string> _texturePathLibrary = new Dictionary<string, string>();
        private Dictionary<string, Color> _colorLibrary = new Dictionary<string, Color>();
        private Dictionary<string, Material> _generatedMaterials = new Dictionary<string, Material>();
        private Dictionary<string, List<string>> _meshSubsets = new Dictionary<string, List<string>>();
        private Dictionary<GameObject, string> _instanceMeshPath = new Dictionary<GameObject, string>();
        private Dictionary<GameObject, string> _singleBindings = new Dictionary<GameObject, string>();
        private Dictionary<GameObject, Dictionary<string, string>> _subsetBindings = new Dictionary<GameObject, Dictionary<string, string>>();

        #endregion

        //=============================================================================
        // SETUP
        //=============================================================================

        #region IMPORTATION

        public override void OnImportAsset(AssetImportContext ctx)
        {
            // Read the first line
            string firstLine = File.ReadLines(assetPath).FirstOrDefault();

            // If it's NOT a Cygon file, force the default Unity USD importer
            if (firstLine != "#usda 1.0 | Cygon")
            {
                return;
            }
            
            _meshCache.Clear();
            _colorLibrary.Clear();
            _texturePathLibrary.Clear();
            _generatedMaterials.Clear();
            _meshSubsets.Clear();
            _instanceMeshPath.Clear();
            _singleBindings.Clear();
            _subsetBindings.Clear();
            
            string rawText = File.ReadAllText(ctx.assetPath);
            string fileName = Path.GetFileNameWithoutExtension(ctx.assetPath);

            if (rawText.Contains("point3f[] points"))
            {
                ImportAsSingleMesh(ctx, rawText, fileName);
            }
            else
            {
                ImportAsScene(ctx, rawText, fileName);
            }
        }
        private void ImportAsSingleMesh(AssetImportContext ctx, string text, string name)
        {
            Mesh mesh = BuildMeshFromUsda(text, out _);
            if (mesh != null)
            {
                GameObject go = new GameObject(name);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>();
                
                ctx.AddObjectToAsset("main", go);
                ctx.AddObjectToAsset("mesh", mesh);
                ctx.SetMainObject(go);
            }
        }
        private void ImportAsScene(AssetImportContext ctx, string text, string name)
        {
            string[] lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            // --- PASS 1: COLLECT MATERIAL & TEXTURE DATA ---
            string currentMatName = "";
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("def Material"))
                {
                    Match m = Regex.Match(trimmed, @"""([^""]+)""");
                    if (m.Success) currentMatName = m.Groups[1].Value;
                }

                if (!string.IsNullOrEmpty(currentMatName))
                {
                    // Capture Color
                    if (trimmed.Contains("color3f inputs:diffuseColor ="))
                        _colorLibrary[currentMatName] = ParseColor(trimmed);

                    // Capture Texture Path
                    if (trimmed.Contains("asset inputs:file = @"))
                    {
                        Match m = Regex.Match(trimmed, @"@([^@]+)@");
                        if (m.Success) _texturePathLibrary[currentMatName] = m.Groups[1].Value;
                    }
                }
            }

            // --- PASS 2: BUILD HIERARCHY ---
            GameObject rootContainer = new GameObject(name);
            Stack<GameObject> parentStack = new Stack<GameObject>();
            parentStack.Push(rootContainer);

            HashSet<int> finalizedTransforms = new HashSet<int>();
            GameObject activeTarget = null;
            
            GameObject lastMeshInstance = null;
            string currentSubset = null;
            
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                // Definition Logic (Filtering out Materials/Shaders from Hierarchy)
                if (trimmed.StartsWith("def "))
                {
                    int fQ = trimmed.IndexOf('"'), lQ = trimmed.LastIndexOf('"');
                    if (fQ != -1 && lQ > fQ)
                    {
                        string objName = trimmed.Substring(fQ + 1, lQ - fQ - 1);
                        if (objName == "Materials" || objName == "PBRShader" || trimmed.Contains("Material") || trimmed.Contains("Shader") || trimmed.Contains("stReader") || trimmed.Contains("texture") || trimmed.Contains("transform2d"))
                        {
                            activeTarget = null;
                            continue;
                        }
                        activeTarget = new GameObject(objName);
                        activeTarget.transform.SetParent(parentStack.Peek().transform);
                        continue;
                    }
                }
                
                if (trimmed.StartsWith("over "))
                {
                    Match om = Regex.Match(trimmed, "\"([^\"]+)\"");
                    string overName = om.Success ? om.Groups[1].Value : null;
                    currentSubset = null;
                    if (overName != null && lastMeshInstance != null &&
                        _instanceMeshPath.TryGetValue(lastMeshInstance, out string mp) &&
                        _meshSubsets.TryGetValue(mp, out List<string> sn) && sn != null && sn.Contains(overName))
                    {
                        currentSubset = overName;
                    }
                    continue;
                }
                
                if (trimmed.Contains("{")) { if (activeTarget != null) parentStack.Push(activeTarget); continue; }
                if (trimmed.Contains("}")) { if (parentStack.Count > 1) parentStack.Pop(); activeTarget = null; currentSubset = null; continue; }
                
                if (trimmed.Contains("rel material:binding"))
                {
                    int lastSlash = trimmed.LastIndexOf('/');
                    int closeBracket = trimmed.LastIndexOf('>');
                    if (lastSlash != -1 && closeBracket > lastSlash && lastMeshInstance != null)
                    {
                        string matName = trimmed.Substring(lastSlash + 1, closeBracket - lastSlash - 1);
                        if (currentSubset != null)
                        {
                            if (!_subsetBindings.TryGetValue(lastMeshInstance, out Dictionary<string, string> binds))
                            {
                                binds = new Dictionary<string, string>();
                                _subsetBindings[lastMeshInstance] = binds;
                            }
                            binds[currentSubset] = matName;
                        }
                        else
                        {
                            _singleBindings[lastMeshInstance] = matName;
                        }
                    }
                    continue;
                }
                
                // Properties
                if (activeTarget != null && activeTarget != rootContainer)
                {
                    int objID = activeTarget.GetInstanceID();

                    if (trimmed.Contains("xformOp:translate") && !finalizedTransforms.Contains(objID + 1))
                    {
                        activeTarget.transform.localPosition = ParseVector3FromLine(trimmed, true);
                        finalizedTransforms.Add(objID + 1);
                    }
                    else if (trimmed.Contains("xformOp:scale") && !finalizedTransforms.Contains(objID + 2))
                    {
                        Vector3 sc = ParseVector3FromLine(trimmed, false);
                        if (activeTarget.transform.parent != null && activeTarget.transform.parent.name == "World") sc = Vector3.one;
                        activeTarget.transform.localScale = sc;
                        finalizedTransforms.Add(objID + 2);
                    }
                    else if (trimmed.Contains("xformOp:rotateZYX") && !finalizedTransforms.Contains(objID + 3))
                    {
                        activeTarget.transform.localEulerAngles = ParseRotationFromLine(trimmed);
                        finalizedTransforms.Add(objID + 3);
                    }
                    else if (trimmed.Contains("prepend references"))
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
            
            ApplyMaterialsToInstances(ctx);
            
            EditorRuntime_USDA.SendLog("green", $"Scene '{name}' : importation is finished.");

            ctx.AddObjectToAsset("main_root", rootContainer);
            ctx.SetMainObject(rootContainer);
        }

        #endregion

        #region HELPERS

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
                    
                    EditorRuntime_USDA.SendLog("green", $"'{inst.name}' : {subMeshCount} materials assigned.");
                }
                else
                {
                    Material m = LoadMaterial(singleMat, ctx);
                    for (int si = 0; si < subMeshCount; si++) mats[si] = m;
                }
                
                renderer.sharedMaterials = mats;
            }
        }
        private Material LoadMaterial(string matName, AssetImportContext ctx)
        {
            if (string.IsNullOrEmpty(matName)) return null;
            
            string materialPath = Path.Combine(Path.GetDirectoryName(ctx.assetPath), "materials", matName + ".mat").Replace('\\', '/');
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat != null) ctx.DependsOnSourceAsset(materialPath);
            return mat;
        }
        private Color ParseColor(string line) {
            Match m = Regex.Match(line, @"\(([^)]+)\)");
            if (m.Success) {
                string[] c = m.Groups[1].Value.Split(',');
                return new Color(float.Parse(c[0].Trim(), CultureInfo.InvariantCulture),
                    float.Parse(c[1].Trim(), CultureInfo.InvariantCulture),
                    float.Parse(c[2].Trim(), CultureInfo.InvariantCulture));
            }
            return Color.white;
        }
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
                else if (trimmed.Contains("int[] indices"))
                {
                    string data = trimmed.Contains("=") ? trimmed.Split('=')[1] : trimmed;
                    foreach (Match m in Regex.Matches(data, @"\d+")) curFaces.Add(int.Parse(m.Value));
                }
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
                // Create a unique key for this vertex's data
                // We use a small multiplier to avoid float precision errors
                // Use 3 decimal places (F3). This is usually the "sweet spot"
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
            
            if (subsetFaces == null || subsetFaces.Count == 0)
            {
                mesh.subMeshCount = 1;
                mesh.SetTriangles(newTriangles, 0);
                return;
            }
            
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

        #endregion

        #region MESH BUILDING

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
                
                MeshRenderer mr = target.GetComponent<MeshRenderer>();
                if (!target.GetComponent<MeshFilter>()) target.AddComponent<MeshFilter>().sharedMesh = mesh;
                if (!mr)
                {
                    mr = target.AddComponent<MeshRenderer>();
                }

                MeshCollider mc = target.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mr.shadowCastingMode = ShadowCastingMode.TwoSided;
            }
        }
        private Mesh BuildMeshFromUsda(string rawText, out List<string> subsetNames)
        {
            subsetNames = new List<string>();
            
            Mesh mesh = new Mesh();
            mesh.name = "USDA_Mesh_Final_Fix";

            List<Vector3> rawPoints = new List<Vector3>();
            List<Vector2> rawUVs = new List<Vector2>();
            List<Vector3> rawNormals = new List<Vector3>();
            List<int> faceIndices = new List<int>();
            List<int> faceVertexCounts = new List<int>();

            string[] lines = rawText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            int currentMode = 0;

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Contains("point3f[] points")) currentMode = 1;
                else if (trimmed.Contains("int[] faceVertexCounts")) currentMode = 5;
                else if (trimmed.Contains("int[] faceVertexIndices")) currentMode = 2;
                else if (trimmed.Contains("float2[] primvars:st")) currentMode = 3;
                else if (trimmed.Contains("normal3f[] normals")) currentMode = 4;
                
                if (currentMode == 1) {
                    foreach (Match m in Regex.Matches(trimmed, @"\(([^)]+)\)")) {
                        string[] c = m.Groups[1].Value.Split(',');
                        rawPoints.Add(new Vector3(float.Parse(c[0], CultureInfo.InvariantCulture), float.Parse(c[1], CultureInfo.InvariantCulture), -float.Parse(c[2], CultureInfo.InvariantCulture)));
                    }
                }
                else if (currentMode == 5) {
                    string data = trimmed.Contains("=") ? trimmed.Split('=')[1] : trimmed;
                    foreach (Match m in Regex.Matches(data, @"\d+")) faceVertexCounts.Add(int.Parse(m.Value));
                }
                else if (currentMode == 2) {
                    string data = trimmed.Contains("=") ? trimmed.Split('=')[1] : trimmed;
                    foreach (Match m in Regex.Matches(data, @"\d+")) faceIndices.Add(int.Parse(m.Value));
                }
                else if (currentMode == 3) {
                    foreach (Match m in Regex.Matches(trimmed, @"\(([^)]+)\)")) {
                        string[] c = m.Groups[1].Value.Split(',');
                        rawUVs.Add(new Vector2(float.Parse(c[0], CultureInfo.InvariantCulture), float.Parse(c[1], CultureInfo.InvariantCulture)));
                    }
                }
                else if (currentMode == 4)
                {
                    foreach (Match m in Regex.Matches(trimmed, @"\(([^)]+)\)")) {
                        string[] c = m.Groups[1].Value.Split(',');
                        // Flip Z to match your point flipping
                        Vector3 norm = new Vector3(
                            float.Parse(c[0], CultureInfo.InvariantCulture),
                            float.Parse(c[1], CultureInfo.InvariantCulture),
                            -float.Parse(c[2], CultureInfo.InvariantCulture)
                        );
                        // Ensure the normal is clean for Unity's lighting engine
                        rawNormals.Add(norm.normalized);
                    }
                }
                if (trimmed.Contains("]")) currentMode = 0;
            }
            
            List<List<int>> subsetFaces = new List<List<int>>();
            ParseGeomSubsets(lines, subsetNames, subsetFaces);
            
            int totalIndices = faceIndices.Count;
            Vector3[] finalVerts = new Vector3[totalIndices];
            Vector2[] finalUVs = new Vector2[totalIndices];
            Vector3[] finalNormals = new Vector3[totalIndices]; // Initialize this correctly
            int[] finalTriangles = new int[totalIndices];

            for (int i = 0; i < totalIndices; i += 3)
            {
                int[] permutation = new int[] { 0, 1, 2 };
                for (int j = 0; j < 3; j++)
                {
                    int oldGlobalIdx = i + j;
                    int newGlobalIdx = i + permutation[j];

                    finalVerts[newGlobalIdx] = rawPoints[faceIndices[oldGlobalIdx]];
                    if (oldGlobalIdx < rawUVs.Count) finalUVs[newGlobalIdx] = rawUVs[oldGlobalIdx];

                    if (oldGlobalIdx < rawNormals.Count)
                    {
                        Vector3 n = rawNormals[oldGlobalIdx].normalized;

                        // Check if the normal is pointing downwards (where the black spots usually are)
                        // n.y < 0 means it's facing the floor.
                        if (n.y < 0.2f)
                        {
                            // We "clamp" the normal so it's at least slightly facing 'up'
                            // This keeps the shadows but prevents them from being 'Void Black'
                            n.y = Mathf.Lerp(n.y, 0.2f, 0.5f);
                            n = n.normalized;
                        }

                        finalNormals[newGlobalIdx] = n;
                    }
                    finalTriangles[newGlobalIdx] = newGlobalIdx;
                }
            }

            mesh.vertices = finalVerts;
            mesh.uv = finalUVs;
            mesh.normals = finalNormals;
            mesh.triangles = finalTriangles;

            // 1. Stitch triangles together (and split into submeshes when subsets exist)
            WeldVertices(mesh, subsetFaces, faceVertexCounts);

            Vector3[] rawWeldedNorms = mesh.normals;
            
            // 2. Temporarily calculate "Perfectly Flat" face normals
            // This gives us the 'ideal' direction for each face
            mesh.RecalculateNormals();
            Vector3[] faceNormals = mesh.normals;

            Vector3[] finalNorms = new Vector3[rawWeldedNorms.Length];

            for (int i = 0; i < rawWeldedNorms.Length; i++)
            {
                // 3. Compare the USDA normal to the Flat Face normal
                // If they are nearly the same, it's a 'Face' -> Leave it alone.
                // If they are different, it's a 'Corner' -> Lift it.
                float similarity = Vector3.Dot(rawWeldedNorms[i], faceNormals[i]);

                if (similarity < 0.99f) // This targets only the corners/edges
                {
                    // Push the corner normal to be more like the flat face normal.
                    // This 'opens up' the crack so it catches light.
                    // Increase 0.6f to 0.8f if corners are still too dark.
                    finalNorms[i] = Vector3.Lerp(rawWeldedNorms[i], faceNormals[i], 0.6f).normalized;
                }
                else
                {
                    // It's a flat face, keep the USDA data exactly as is
                    finalNorms[i] = rawWeldedNorms[i];
                }
            }

            mesh.normals = finalNorms;
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            return mesh;
        }

        #endregion
    }
}

