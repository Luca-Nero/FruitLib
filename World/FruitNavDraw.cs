using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// Diagnostics view of FruitNav (FruitLibConfig.NavDraw): the navmesh within 50 m of the
    /// camera as a teal fill with cyan edges, and the path every walking body is following
    /// (yellow, orange where it is tight, red when it is partial).
    ///
    /// The triangles the navmesh hands back are its simplified polygons, which float over or
    /// sink into uneven ground by a good few centimetres. They are split into pieces under
    /// 0.8 m and each corner is dropped onto the real ground with a ray, so the view shows
    /// where the walkable area is, not how coarse its polygons are.
    /// </summary>
    /// <remarks>Since 5.19.0.</remarks>
    internal static class FruitNavDraw
    {
        private const float Radius = 50f, MaxEdge = 0.8f, Lift = 0.04f;

        private static GameObject _fill, _edges;
        private static Material _fillMat, _edgeMat;
        private static int _drawnVersion = -1;
        private static Vector3 _drawnAt;
        private static readonly Dictionary<int, LineRenderer> _lines = new Dictionary<int, LineRenderer>();
        private static readonly Dictionary<int, int> _lineColour = new Dictionary<int, int>();
        private static Material[] _pathMats;
        private static readonly List<int> _gone = new List<int>();

        internal static void Tick()
        {
            if (!FruitLibConfig.NavDraw && !FruitLibConfig.NavProbe) { if (_fill != null || _lines.Count > 0) Clear(); return; }
            if (!FruitNav.Ready || !FruitBodies.HasView) return;

            Vector3 cam = FruitBodies.ViewPosition;
            if (FruitNav.Version != _drawnVersion || FruitNav.Flat(cam - _drawnAt) > 15f)
            {
                _drawnVersion = FruitNav.Version;
                _drawnAt = cam;
                try { Mesh(cam); }
                catch (Exception e) { FruitBodies.ReportOnce("navmesh drawing", e); }
            }
            try { Paths(); }
            catch (Exception e) { FruitBodies.ReportOnce("path drawing", e); }
        }

        internal static void Clear()
        {
            if (_fill != null) Object.Destroy(_fill);
            if (_edges != null) Object.Destroy(_edges);
            _fill = _edges = null;
            foreach (var l in _lines.Values) if (l != null) Object.Destroy(l.gameObject);
            _lines.Clear();
            _lineColour.Clear();
            _drawnVersion = -1;
        }

        private static Material Unlit(Color c)
        {
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Universal Render Pipeline/Unlit");
            return new Material(sh) { color = c };
        }

        // ── The navmesh ──

        private static readonly List<Vector3> _verts = new List<Vector3>();
        private static readonly List<int> _tris = new List<int>(), _lineIdx = new List<int>();

        private static void Mesh(Vector3 cam)
        {
            FruitNavNative.Triangulate(out var v, out var idx, out _);
            _verts.Clear(); _tris.Clear(); _lineIdx.Clear();
            float r2 = Radius * Radius;

            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                Vector3 a = v[idx[t]], b = v[idx[t + 1]], c = v[idx[t + 2]];
                float dx = a.x - cam.x, dz = a.z - cam.z;
                if (dx * dx + dz * dz > r2) continue;
                Split(a, b, c, 0);
                EdgeLine(a, b); EdgeLine(b, c); EdgeLine(c, a);
            }
            if (_tris.Count == 0) { if (_fill != null) _fill.SetActive(false); return; }

            _fillMat ??= Unlit(new Color(0f, 0.35f, 0.32f));
            _edgeMat ??= Unlit(new Color(0.3f, 1f, 1f));
            var fm = MeshOn(ref _fill, "FruitNav_Fill", _fillMat);
            fm.Clear();
            fm.vertices = _verts.ToArray();
            fm.triangles = _tris.ToArray();
            fm.RecalculateBounds();

            // Edges reuse the fill's vertex list: segment ends were appended to it as pairs.
            var em = MeshOn(ref _edges, "FruitNav_Edges", _edgeMat);
            em.Clear();
            em.vertices = _verts.ToArray();
            em.SetIndices(_lineIdx.ToArray(), MeshTopology.Lines, 0);
            em.RecalculateBounds();
        }

        private static Mesh MeshOn(ref GameObject go, string name, Material mat)
        {
            if (go == null)
            {
                go = new GameObject(name);
                Object.DontDestroyOnLoad(go);
                var mr = go.AddComponent<MeshRenderer>();
                mr.material = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                var mf = go.AddComponent<MeshFilter>();
                mf.mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            }
            go.SetActive(true);
            return go.GetComponent<MeshFilter>().mesh;
        }

        /// <summary>Splits a triangle until its edges are short, then adds it with its corners on the ground.</summary>
        private static void Split(Vector3 a, Vector3 b, Vector3 c, int depth)
        {
            // Plain float maths throughout: Vector3 operators are native calls in this interop.
            float ab = D2(a, b), bc = D2(b, c), ca = D2(c, a);
            float m = Math.Max(ab, Math.Max(bc, ca));
            if (m > MaxEdge * MaxEdge && depth < 5)
            {
                // Halve the longest edge.
                if (m == ab) { var h = Mid(a, b, 0.5f); Split(a, h, c, depth + 1); Split(h, b, c, depth + 1); }
                else if (m == bc) { var h = Mid(b, c, 0.5f); Split(a, b, h, depth + 1); Split(a, h, c, depth + 1); }
                else { var h = Mid(c, a, 0.5f); Split(a, b, h, depth + 1); Split(h, b, c, depth + 1); }
                return;
            }
            int i = _verts.Count;
            _verts.Add(Drop(a)); _verts.Add(Drop(b)); _verts.Add(Drop(c));
            _tris.Add(i); _tris.Add(i + 1); _tris.Add(i + 2);
        }

        private static void EdgeLine(Vector3 a, Vector3 b)
        {
            int n = Math.Min(64, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(D2(a, b)) / MaxEdge)));
            Vector3 prev = Drop(a);
            prev.y += 0.02f;
            for (int k = 1; k <= n; k++)
            {
                Vector3 p = Drop(Mid(a, b, (float)k / n));
                p.y += 0.02f;
                int i = _verts.Count;
                _verts.Add(prev); _verts.Add(p);
                _lineIdx.Add(i); _lineIdx.Add(i + 1);
                prev = p;
            }
        }

        private static float D2(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x, y = a.y - b.y, z = a.z - b.z;
            return x * x + y * y + z * z;
        }

        private static Vector3 Mid(Vector3 a, Vector3 b, float t)
        {
            Vector3 r = a;
            r.x += (b.x - a.x) * t; r.y += (b.y - a.y) * t; r.z += (b.z - a.z) * t;
            return r;
        }

        /// <summary>Onto the ground under (or just over) a navmesh point.</summary>
        private static Vector3 Drop(Vector3 p)
        {
            Vector3 from = p;
            from.y += 0.5f;
            if (Physics.Raycast(from, Vector3.down, out RaycastHit h, 1.0f, FruitNav.Layers, QueryTriggerInteraction.Ignore))
                p.y = h.point.y;
            p.y += Lift;
            return p;
        }

        // ── Paths ──

        private static void Paths()
        {
            _gone.Clear();
            foreach (var kv in _lines)
            {
                var b = FruitBodies.Get(kv.Key);
                if (b == null || FruitBodyControl.PathOf(b) == null) _gone.Add(kv.Key);
            }
            foreach (var id in _gone) { if (_lines[id] != null) Object.Destroy(_lines[id].gameObject); _lines.Remove(id); _lineColour.Remove(id); }
            _pathMats ??= new[] { Unlit(new Color(1f, 0.9f, 0.1f)), Unlit(new Color(1f, 0.5f, 0f)), Unlit(new Color(1f, 0.15f, 0.1f)) };

            foreach (var b in FruitBodies.All)
            {
                var p = FruitBodyControl.PathOf(b);
                if (p == null) continue;
                if (!_lines.TryGetValue(b.Id, out var lr) || lr == null)
                {
                    lr = new GameObject("FruitNav_Path").AddComponent<LineRenderer>();
                    Object.DontDestroyOnLoad(lr.gameObject);
                    lr.useWorldSpace = true;
                    lr.widthMultiplier = 0.05f;
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    _lines[b.Id] = lr;
                }
                bool tight = p.Room.Count > 0 && p.RoomAhead(0f, p.Length) < FruitBodyControl.TightRoom;
                int col = p.Status == FruitPathStatus.Partial ? 2 : tight ? 1 : 0;
                if (!_lineColour.TryGetValue(b.Id, out int had) || had != col) { lr.material = _pathMats[col]; _lineColour[b.Id] = col; }
                lr.positionCount = p.Corners.Count;
                for (int i = 0; i < p.Corners.Count; i++) { var c = p.Corners[i]; c.y += 0.15f; lr.SetPosition(i, c); }
            }
        }
    }
}
