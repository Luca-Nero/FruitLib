using MelonLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Vector3 = UnityEngine.Vector3;

namespace FruitLib
{

    public class FruitMaterialGroup
    {
        public string Name;
        public float[] Kd;      // [r, g, b]
        public float Alpha;
        public int TriStart;    // index into the flat tris array
        public int TriCount;    // number of indices (not triangles, thanks past luca)
    }

    public class FruitMeshEntry
    {
        public string Name;
        public Mesh Mesh;
        public FruitMaterialGroup[] Materials;   // empty array if the JSON carried none
    }


    public class FruitMeshLibrary
    {
        private readonly List<FruitMeshEntry> _entries = new List<FruitMeshEntry>();

        public int Count => _entries.Count;

        public FruitMeshLibrary(Assembly assembly, string suffix = "_mesh.json")
        {
            var resources = assembly.GetManifestResourceNames()
                .Where(n => n.EndsWith(suffix)).ToArray();
            if (resources.Length == 0)
            {
                MelonLogger.Warning($"[FruitMeshLoader] No *{suffix} embedded resources found in '{assembly.GetName().Name}'.");
                return;
            }

            foreach (var resName in resources)
            {
                string json;
                using (var stream = assembly.GetManifestResourceStream(resName))
                using (var reader = new StreamReader(stream))
                    json = reader.ReadToEnd();

                var data = FruitMeshJson.Parse(json);
                if (data == null) { MelonLogger.Warning($"[FruitMeshLoader] Parse failed: {resName}"); continue; }

                var mesh = BuildMesh(data, resName);
                if (mesh == null) continue;

                string name = StripResourceName(resName, suffix);
                _entries.Add(new FruitMeshEntry
                {
                    Name = name,
                    Mesh = mesh,
                    Materials = data.Materials ?? Array.Empty<FruitMaterialGroup>(),
                });
            }

            MelonLogger.Msg($"[FruitMeshLoader] {_entries.Count} mesh(es) ready from '{assembly.GetName().Name}'.");
        }

        public Mesh GetMesh(string name)
        {
            if (string.IsNullOrEmpty(name)) return _entries.Count > 0 ? _entries[0].Mesh : null;
            foreach (var e in _entries)
                if (e.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return e.Mesh;
            MelonLogger.Warning($"[FruitMeshLoader] Mesh '{name}' not found, {_entries.Count} loaded");
            return null;
        }

        public FruitMaterialGroup[] GetMaterials(string name)
        {
            if (string.IsNullOrEmpty(name) && _entries.Count > 0) name = _entries[0].Name;
            if (name == null) return Array.Empty<FruitMaterialGroup>();
            foreach (var e in _entries)
                if (e.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return e.Materials;
            return Array.Empty<FruitMaterialGroup>();
        }

        private static string StripResourceName(string resName, string suffix)
        {
            int lastDot = resName.LastIndexOf(suffix, StringComparison.Ordinal);
            if (lastDot < 0) return resName;
            int keepSuffixLen = suffix.StartsWith("_mesh", StringComparison.Ordinal) ? "_mesh".Length : 0;
            int prefixEnd = resName.LastIndexOf('.', Math.Max(lastDot - 1, 0));
            return prefixEnd >= 0
                ? resName.Substring(prefixEnd + 1, lastDot + keepSuffixLen - prefixEnd - 1)
                : resName.Substring(0, lastDot + keepSuffixLen);
        }

        private static Mesh BuildMesh(FruitMeshJson data, string name)
        {
            try
            {
                var mesh = new Mesh();
                mesh.name = name;
                mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                mesh.indexFormat = IndexFormat.UInt32;
                mesh.vertices = data.Verts;
                if (data.Normals.Length > 0) mesh.normals = data.Normals;
                if (data.UVs.Length > 0) mesh.uv = data.UVs;

                if (data.Materials != null && data.Materials.Length > 1)
                {
                    mesh.subMeshCount = data.Materials.Length;
                    for (int s = 0; s < data.Materials.Length; s++)
                    {
                        var mg = data.Materials[s];
                        mesh.SetTriangles(GetSubArray(data.Tris, mg.TriStart, mg.TriCount), s);
                    }
                }
                else
                {
                    mesh.triangles = data.Tris;
                }

                mesh.RecalculateBounds();
                mesh.RecalculateNormals();
                return mesh;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMeshLoader] Error building '{name}': {e.Message}"); return null; }
        }

        private static int[] GetSubArray(int[] src, int start, int count)
        {
            var sub = new int[count];
            Array.Copy(src, start, sub, 0, count);
            return sub;
        }
    }

    public static class FruitMeshUtil
    {
        public static void ApplyMaterials(Renderer renderer, FruitMaterialGroup[] groups,
            Shader shader, Color fallbackColor)
        {
            if (groups != null && groups.Length > 1)
            {
                var mats = new Material[groups.Length];
                for (int m = 0; m < groups.Length; m++)
                {
                    mats[m] = new Material(shader);
                    mats[m].color = new Color(groups[m].Kd[0], groups[m].Kd[1], groups[m].Kd[2], groups[m].Alpha);
                }
                renderer.materials = mats;
            }
            else if (groups != null && groups.Length == 1)
            {
                renderer.material = new Material(shader);
                renderer.material.color = new Color(groups[0].Kd[0], groups[0].Kd[1], groups[0].Kd[2], groups[0].Alpha);
            }
            else
            {
                renderer.material = new Material(shader);
                renderer.material.color = fallbackColor;
            }
        }
    }

    internal class FruitMeshJson
    {
        public Vector3[] Verts; public Vector3[] Normals; public Vector2[] UVs; public int[] Tris;
        public FruitMaterialGroup[] Materials;

        public static FruitMeshJson Parse(string json)
        {
            try
            {
                return new FruitMeshJson
                {
                    Verts = ParseVec3Array(ExtractArray(json, "verts")),
                    Normals = ParseVec3Array(ExtractArray(json, "normals")),
                    UVs = ParseVec2Array(ExtractArray(json, "uvs")),
                    Tris = ParseIntArray(ExtractArray(json, "tris")),
                    Materials = ParseMaterials(json)
                };
            }
            catch (Exception e) { MelonLogger.Warning("[FruitMeshJson] " + e.Message); return null; }
        }

        // ── Material parsing ──────────────────────────────────────────────────

        private static FruitMaterialGroup[] ParseMaterials(string json)
        {
            var inner = ExtractArray(json, "materials");
            if (inner == null) return Array.Empty<FruitMaterialGroup>();

            var groups = new List<FruitMaterialGroup>();
            int i = 0;
            while (i < inner.Length)
            {
                if (inner[i] == '{')
                {
                    int start = i;
                    int depth = 1;
                    i++;
                    while (i < inner.Length && depth > 0)
                    {
                        if (inner[i] == '{') depth++;
                        else if (inner[i] == '}') depth--;
                        i++;
                    }
                    string obj = inner.Substring(start, i - start);
                    groups.Add(ParseOneMaterial(obj));
                }
                else i++;
            }
            return groups.ToArray();
        }

        private static FruitMaterialGroup ParseOneMaterial(string obj)
        {
            var g = new FruitMaterialGroup();
            g.Name = ExtractString(obj, "name") ?? "default";
            g.Alpha = ExtractFloat(obj, "alpha", 1f);
            g.TriStart = (int)ExtractFloat(obj, "triStart", 0);
            g.TriCount = (int)ExtractFloat(obj, "triCount", 0);

            // Parse kd array: "kd":[r,g,b]
            var kdInner = ExtractArray(obj, "kd");
            if (kdInner != null)
            {
                var parts = kdInner.Split(',');
                g.Kd = new float[] {
                    float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                };
            }
            else
            {
                g.Kd = new float[] { 0.5f, 0.5f, 0.5f };
            }

            return g;
        }

        private static string ExtractString(string json, string key)
        {
            string marker = "\"" + key + "\":\"";
            int s = json.IndexOf(marker);
            if (s < 0) return null;
            s += marker.Length;
            int e = json.IndexOf('"', s);
            return e > s ? json.Substring(s, e - s) : null;
        }

        private static float ExtractFloat(string json, string key, float fallback)
        {
            string marker = "\"" + key + "\":";
            int s = json.IndexOf(marker);
            if (s < 0) return fallback;
            s += marker.Length;
            int e = s;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '.' || json[e] == '-'))
                e++;
            if (e == s) return fallback;
            return float.Parse(json.Substring(s, e - s), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string ExtractArray(string json, string key)
        {
            string m = "\"" + key + "\":[";
            int s = json.IndexOf(m);
            if (s < 0) return null;
            s += m.Length;
            int d = 1, i = s;
            while (i < json.Length && d > 0)
            {
                if (json[i] == '[') d++;
                else if (json[i] == ']') d--;
                if (d > 0) i++;
            }
            return json.Substring(s, i - s);
        }

        private static List<string> SplitSubArrays(string inner)
        {
            var t = new List<string>();
            int i = 0;
            while (i < inner.Length)
            {
                if (inner[i] == '[')
                {
                    int d = 0, s = i;
                    while (i < inner.Length)
                    {
                        if (inner[i] == '[') d++;
                        else if (inner[i] == ']')
                        {
                            d--;
                            if (d == 0) { t.Add(inner.Substring(s, i - s + 1)); i++; break; }
                        }
                        i++;
                    }
                }
                else i++;
            }
            return t;
        }

        private static float[] ParseFloats(string b)
        {
            var inner = b.Trim('[', ']');
            var p = inner.Split(',');
            var r = new float[p.Length];
            for (int i = 0; i < p.Length; i++)
                r[i] = float.Parse(p[i].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            return r;
        }

        private static Vector3[] ParseVec3Array(string inner)
        {
            if (inner == null) return Array.Empty<Vector3>();
            var t = SplitSubArrays(inner);
            var r = new Vector3[t.Count];
            for (int i = 0; i < t.Count; i++) { var f = ParseFloats(t[i]); r[i] = new Vector3(f[0], f[1], f[2]); }
            return r;
        }

        private static Vector2[] ParseVec2Array(string inner)
        {
            if (inner == null) return Array.Empty<Vector2>();
            var t = SplitSubArrays(inner);
            var r = new Vector2[t.Count];
            for (int i = 0; i < t.Count; i++) { var f = ParseFloats(t[i]); r[i] = new Vector2(f[0], f[1]); }
            return r;
        }

        private static int[] ParseIntArray(string inner)
        {
            if (inner == null || inner.Trim().Length == 0) return Array.Empty<int>();
            var p = inner.Split(',');
            var r = new int[p.Length];
            for (int i = 0; i < p.Length; i++) r[i] = int.Parse(p[i].Trim());
            return r;
        }
    }
}
