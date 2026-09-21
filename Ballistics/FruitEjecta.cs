using System.Collections;
using System.Collections.Generic;
using Il2CppVoxelMeshGeneration;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// What comes out of an exit wound: small tissue-coloured chunks that fly, stick to what
    /// they hit and leave blood decals that wrap onto the surface. Moved here from FruitTweaks
    /// (WoundEjectVFX / BloodDecalAtlas) so every projectile and fragment gets it, gated per
    /// wound by <see cref="WoundProfile.Ejecta"/> and globally by the Ballistics settings.
    ///
    /// Frame-pressure aware: counts shrink as FruitPerfMon's pressure rises, and the oldest
    /// chunks and decals are evicted when the frame rate falls under the target.
    /// </summary>
    internal static class FruitEjecta
    {
        private const string Tag = "[FruitEjecta]";
        private const int ChunkLayer = 2;   // Ignore Raycast: rounds and other chunks pass through

        internal static bool Enabled   => FruitHudConfig.Ejecta;
        internal static int  MaxPerWound => Mathf.Max(0, FruitHudConfig.EjectaMaxCount);
        internal static int  MinDepth    => Mathf.Max(1, FruitHudConfig.EjectaMinDepth);

        /// <summary>
        /// Chunks for one exit wound, from the power the round carried out of it. What blows
        /// tissue out of the far side is the energy still behind the round, so a buckshot
        /// pellet leaving an arm throws a chunk or two and a .50 throws the full count -
        /// instead of every perforation throwing the same, which made a 12-pellet shot
        /// spray more than a rifle. At least one: a perforation is still a perforation.
        /// </summary>
        internal static int CountFor(int powerOut)
        {
            float full = Mathf.Max(1f, FruitHudConfig.EjectaFullPower);
            return Mathf.Clamp(Mathf.CeilToInt(MaxPerWound * Mathf.Clamp01(powerOut / full)), 1, MaxPerWound);
        }

        internal static readonly Color Muscle = new Color(0.42f, 0.05f, 0.05f);

        private static readonly System.Random _rng = new System.Random();
        private static bool _layerSetup;

        private sealed class Handle { public bool Evict; }
        private static readonly LinkedList<Handle> _chunks = new LinkedList<Handle>();
        private static readonly LinkedList<Handle> _decals = new LinkedList<Handle>();

        /// <summary>A voxel's actual colour, or muscle red for an unwritten (all-zero) one -
        /// a black chunk reads as a rendering bug, not as gore.</summary>
        internal static Color TissueColour(RGBAtlasColor c)
        {
            try
            {
                Color32 v = c.value;
                if (v.r == 0 && v.g == 0 && v.b == 0) return Muscle;
                return new Color(v.r / 255f, v.g / 255f, v.b / 255f);
            }
            catch { return Muscle; }
        }

        // ── Eviction ─────────────────────────────────────────────────────────────

        internal static void Tick()
        {
            if (_chunks.Count == 0 && _decals.Count == 0) return;

            float pressure = FruitPerfMon.PressureLevel;
            float fps      = FruitPerfMon.LongFps;
            float target   = FruitHudConfig.EjectaTargetFps;

            int evict = pressure > 0.25f ? Mathf.RoundToInt(Mathf.Clamp01((pressure - 0.25f) / 0.75f) * 10f) : 0;
            if (fps > 0f && fps < target)
                evict = Mathf.Max(evict, Mathf.RoundToInt((target - fps) * FruitHudConfig.EjectaCullSpeed));
            if (evict <= 0) return;

            Mark(_decals, evict);
            Mark(_chunks, Mathf.Max(evict / 2, 1));
        }

        private static void Mark(LinkedList<Handle> queue, int n)
        {
            var node = queue.First;
            for (int i = 0; i < n && node != null; i++, node = node.Next) node.Value.Evict = true;
        }

        // ── Chunks ───────────────────────────────────────────────────────────────

        internal static void Spawn(Vector3 exit, Vector3 dir, List<Color> colours, Rigidbody host)
        {
            if (!Enabled || colours.Count == 0) return;
            if (!_layerSetup) { Physics.IgnoreLayerCollision(ChunkLayer, ChunkLayer, true); _layerSetup = true; }

            Collider[] ownBody = host != null ? host.transform.root.GetComponentsInChildren<Collider>() : null;
            int max = Mathf.RoundToInt(MaxPerWound * Mathf.Clamp01(1f - FruitPerfMon.PressureLevel));
            int n = Mathf.Min(colours.Count, max);
            for (int i = 0; i < n; i++)
                MelonCoroutines.Start(Chunk(exit, dir, colours[i], ownBody));
        }

        private static IEnumerator Chunk(Vector3 p0, Vector3 forward, Color col, Collider[] ownBody)
        {
            var handle = new Handle();
            var node = _chunks.AddLast(handle);

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "FruitLib_Ejecta";
            go.layer = ChunkLayer;
            go.transform.localScale = Vector3.one * 0.05f;
            go.transform.position = p0;
            var box = go.GetComponent<BoxCollider>();
            if (box != null) box.enabled = false;

            var mat = new Material(Shader.Find("Unlit/Color")) { color = col };
            go.GetComponent<Renderer>().material = mat;

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.005f;
            rb.drag = 0.3f;
            rb.angularDrag = 0.5f;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            Vector3 spread = Random.insideUnitSphere * FruitHudConfig.EjectaSpread;
            rb.linearVelocity  = (forward + spread).normalized * FruitHudConfig.EjectaSpeed * (0.5f + (float)_rng.NextDouble());
            rb.angularVelocity = Random.onUnitSphere * Random.Range(5f, 15f);

            // A beat before the collider comes on, so it does not collide with the body it
            // is leaving; and then never with that body at all.
            yield return new WaitForSeconds(0.1f);
            if (go == null || handle.Evict) { Finish(node, go, mat); yield break; }
            if (box != null)
            {
                box.enabled = true;
                if (ownBody != null) foreach (var c in ownBody) if (c != null) Physics.IgnoreCollision(box, c);
            }

            bool stuck = false;
            Rigidbody host = null;
            Vector3 local = Vector3.zero;
            float t = 0f;

            while (t < 2f && go != null && !stuck && !handle.Evict)
            {
                t += Time.deltaTime;
                Vector3 v = rb.linearVelocity;
                float speed = v.magnitude;

                if (speed > 2f)
                {
                    if (Physics.Raycast(go.transform.position, v / speed, out RaycastHit hit, speed * Time.deltaTime + 0.02f)
                        && hit.collider.gameObject != go)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.isKinematic = true;

                        Decals(hit.point, hit.normal, hit.collider.transform);

                        if (FruitWounds.IsLimb(hit.collider.gameObject))
                        {
                            var limbRb = hit.collider.GetComponentInParent<Rigidbody>();
                            if (limbRb != null)
                            {
                                stuck = true;
                                host = limbRb;
                                local = host.transform.InverseTransformPoint(hit.point);
                                if (box != null) box.enabled = false;
                            }
                        }
                    }
                }
                else if (speed < 0.05f) { rb.isKinematic = true; break; }

                yield return null;
            }

            if (go == null || handle.Evict) { Finish(node, go, mat); yield break; }
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.isKinematic = true; }

            Vector3 full = go.transform.localScale;
            float life = FruitHudConfig.EjectaLifetime, fade = life * 0.65f, e = 0f;
            while (e < life && go != null && !handle.Evict)
            {
                e += Time.deltaTime;
                if (stuck && host != null) go.transform.position = host.transform.TransformPoint(local);
                if (e > fade) go.transform.localScale = Vector3.Lerp(full, Vector3.zero, (e - fade) / (life - fade));
                yield return null;
            }

            Finish(node, go, mat);
        }

        private static void Finish(LinkedListNode<Handle> node, GameObject go, Material mat)
        {
            if (node.List != null) node.List.Remove(node);
            if (go != null) Object.Destroy(go);
            if (mat != null) Object.Destroy(mat);
        }

        // ── Decals ───────────────────────────────────────────────────────────────

        private static Texture2D _atlas;
        private static Shader    _shader;
        private static Material  _material;
        private static bool      _atlasTried;

        private static bool EnsureAtlas()
        {
            if (_material != null) return true;
            if (_atlasTried) return false;
            _atlasTried = true;

            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>()) if (t.name == "Pixelblood") { _atlas = t; break; }
            foreach (var s in Resources.FindObjectsOfTypeAll<Shader>()) if (s.name == "Sprites/Default") { _shader = s; break; }

            if (_atlas == null || _shader == null)
            {
                MelonLogger.Warning($"{Tag} Pixelblood atlas or Sprites/Default shader not found; blood decals off");
                return false;
            }

            _material = new Material(_shader) { mainTexture = _atlas, color = Muscle, renderQueue = 3000 };
            Object.DontDestroyOnLoad(_material);
            return true;
        }

        private static void Decals(Vector3 point, Vector3 normal, Transform surface)
        {
            if (!FruitHudConfig.BloodDecals || !EnsureAtlas()) return;

            Vector3 tangent = Vector3.Cross(normal, Vector3.up);
            if (tangent.sqrMagnitude < 0.01f) tangent = Vector3.Cross(normal, Vector3.forward);
            tangent.Normalize();
            Vector3 bitangent = Vector3.Cross(tangent, normal).normalized;

            float radius = FruitHudConfig.BloodDecalSize;
            int target = Mathf.RoundToInt(FruitHudConfig.BloodDecalCount * Mathf.Clamp01(1f - FruitPerfMon.PressureLevel * 2f));
            int placed = 0;

            for (int attempt = 0; attempt < target * 3 && placed < target; attempt++)
            {
                float a = (float)_rng.NextDouble() * Mathf.PI * 2f;
                float d = radius * Mathf.Pow((float)_rng.NextDouble(), 0.7f) * (0.3f + (float)_rng.NextDouble() * 1.4f);
                Vector3 candidate = point + tangent * (Mathf.Cos(a) * d) + bitangent * (Mathf.Sin(a) * d);

                if (!Physics.Raycast(candidate + normal * 0.05f, -normal, out RaycastHit hit, 0.15f)) continue;

                float size = radius * 0.35f * (0.7f + Random.value * 0.6f);
                float rotZ = Random.Range(0f, 360f);
                if (!BuildDecalMesh(hit, size * 0.5f, rotZ, out var mesh)) continue;

                MelonCoroutines.Start(Decal(hit.point, hit.normal, surface, mesh, rotZ));
                placed++;
            }
        }

        /// <summary>
        /// A quad if all four corners have surface under them; otherwise a 6x6 grid keeping only
        /// the cells that do, so a decal on an edge wraps off it instead of hanging in the air.
        /// </summary>
        private static bool BuildDecalMesh(RaycastHit hit, float h, float rotZ, out Mesh mesh)
        {
            const int grid = 6;
            const float cast = 0.05f;
            mesh = null;

            Quaternion rot = Quaternion.FromToRotation(Vector3.forward, hit.normal) * Quaternion.Euler(0f, 0f, rotZ);
            Rect uv = AtlasTile();
            int gv = grid + 1;
            var has = new bool[gv * gv];
            for (int j = 0; j < gv; j++)
            for (int i = 0; i < gv; i++)
            {
                Vector3 wp = hit.point + rot * new Vector3(Mathf.Lerp(-h, h, i / (float)grid), Mathf.Lerp(-h, h, j / (float)grid), 0f);
                has[j * gv + i] = Physics.Raycast(wp + hit.normal * 0.02f, -hit.normal, cast + 0.02f);
            }

            var verts = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var tris  = new List<int>();
            var map   = new int[gv * gv];
            for (int k = 0; k < map.Length; k++) map[k] = -1;

            int Vertex(int idx)
            {
                if (map[idx] >= 0) return map[idx];
                int gi = idx % gv, gj = idx / gv;
                map[idx] = verts.Count;
                verts.Add(new Vector3(Mathf.Lerp(-h, h, gi / (float)grid), Mathf.Lerp(-h, h, gj / (float)grid), 0f));
                uvs.Add(new Vector2(Mathf.Lerp(uv.xMin, uv.xMax, gi / (float)grid), Mathf.Lerp(uv.yMin, uv.yMax, gj / (float)grid)));
                return map[idx];
            }

            for (int j = 0; j < grid; j++)
            for (int i = 0; i < grid; i++)
            {
                int a = j * gv + i, b = a + 1, c = a + gv + 1, d = a + gv;
                if (!(has[a] && has[b] && has[c] && has[d])) continue;
                int va = Vertex(a), vb = Vertex(b), vc = Vertex(c), vd = Vertex(d);
                tris.Add(va); tris.Add(vb); tris.Add(vc);
                tris.Add(va); tris.Add(vc); tris.Add(vd);
            }
            if (tris.Count == 0) return false;

            mesh = new Mesh { vertices = verts.ToArray(), uv = uvs.ToArray(), triangles = tris.ToArray() };
            mesh.RecalculateNormals();
            return true;
        }

        private static Rect AtlasTile()
        {
            int cols = Mathf.Max(1, FruitHudConfig.BloodAtlasCols), rows = Mathf.Max(1, FruitHudConfig.BloodAtlasRows);
            int idx = _rng.Next(cols * rows);
            return new Rect((idx % cols) / (float)cols, (idx / cols) / (float)rows, 1f / cols, 1f / rows);
        }

        private static IEnumerator Decal(Vector3 point, Vector3 normal, Transform surface, Mesh mesh, float rotZ)
        {
            var handle = new Handle();
            var node = _decals.AddLast(handle);

            var go = new GameObject("FruitLib_BloodDecal");
            go.transform.position = point + normal * 0.001f;
            go.transform.rotation = Quaternion.FromToRotation(Vector3.forward, normal) * Quaternion.Euler(0f, 0f, rotZ);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _material;
            mr.receiveShadows = false;
            if (surface != null) go.transform.SetParent(surface, true);

            Vector3 full = go.transform.localScale;
            float life = FruitHudConfig.BloodDecalLifetime, fade = life * 0.7f, t = 0f;
            while (t < life && go != null && !handle.Evict)
            {
                t += Time.deltaTime;
                if (t > fade) go.transform.localScale = Vector3.Lerp(full, Vector3.zero, (t - fade) / (life - fade));
                yield return null;
            }

            if (node.List != null) node.List.Remove(node);
            if (mesh != null) Object.Destroy(mesh);
            if (go != null) Object.Destroy(go);
        }
    }
}
