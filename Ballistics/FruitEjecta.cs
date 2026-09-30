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

        internal static bool Enabled   => FruitLibConfig.Ejecta;
        internal static int  MaxPerWound => Mathf.Max(0, FruitLibConfig.EjectaMaxCount);
        internal static int  MinDepth    => Mathf.Max(1, FruitLibConfig.EjectaMinDepth);

        /// <summary>
        /// Chunks for one exit wound, from the power the round carried out of it. What blows
        /// tissue out of the far side is the energy still behind the round, so a buckshot
        /// pellet leaving an arm throws a chunk or two and a .50 throws the full count -
        /// instead of every perforation throwing the same, which made a 12-pellet shot
        /// spray more than a rifle. At least one: a perforation is still a perforation.
        /// </summary>
        internal static int CountFor(int powerOut)
        {
            float full = Mathf.Max(1f, FruitLibConfig.EjectaFullPower);
            return Mathf.Clamp(Mathf.CeilToInt(MaxPerWound * Mathf.Clamp01(powerOut / full)), 1, MaxPerWound);
        }

        internal static readonly Color Muscle = new Color(0.42f, 0.05f, 0.05f);

        private static readonly System.Random _rng = new System.Random();
        // Chunks on layer 2 must not collide with each other, but that is one global matrix
        // cell shared with everything else the game keeps on Ignore Raycast. So the cell is
        // flipped only while a chunk is alive, and put back to what the game had after.
        private static bool _layerScoped;    // we have changed the cell and owe it back
        private static bool _layerOriginal;  // what the game had before we touched it
        private static bool _layerWarned;

        private sealed class Handle { public bool Evict; }
        private static readonly LinkedList<Handle> _chunks = new LinkedList<Handle>();
        private static readonly LinkedList<Handle> _decals = new LinkedList<Handle>();

        internal static int ChunkCount => _chunks.Count;
        internal static int DecalCount => _decals.Count;

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
            float target   = FruitLibConfig.EjectaTargetFps;

            int evict = pressure > 0.25f ? Mathf.RoundToInt(Mathf.Clamp01((pressure - 0.25f) / 0.75f) * 10f) : 0;
            if (fps > 0f && fps < target)
                evict = Mathf.Max(evict, Mathf.RoundToInt((target - fps) * FruitLibConfig.EjectaCullSpeed));
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
            Collider[] ownBody = host != null ? host.transform.root.GetComponentsInChildren<Collider>() : null;
            int max = Mathf.RoundToInt(MaxPerWound * Mathf.Clamp01(1f - FruitPerfMon.PressureLevel));
            int n = Mathf.Min(colours.Count, max);
            for (int i = 0; i < n; i++)
                MelonCoroutines.Start(Chunk(exit, dir, colours[i], ownBody));
        }

        private static IEnumerator Chunk(Vector3 p0, Vector3 forward, Color col, Collider[] ownBody)
        {
            if (!Build(p0, forward, col, out var go, out var box, out var mat, out var rb)) yield break;

            var handle = new Handle();
            var node = _chunks.AddLast(handle);
            IgnoreChunkCollisions();

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
            float life = FruitLibConfig.EjectaLifetime, fade = life * 0.65f, e = 0f;
            while (e < life && go != null && !handle.Evict)
            {
                e += Time.deltaTime;
                if (stuck && host != null) go.transform.position = host.transform.TransformPoint(local);
                if (e > fade) go.transform.localScale = Vector3.Lerp(full, Vector3.zero, (e - fade) / (life - fade));
                yield return null;
            }

            Finish(node, go, mat);
        }

        /// <summary>
        /// One chunk, launched. False, with nothing left behind, if any step of it fails.
        ///
        /// Only setters the Release build still has: collisionDetectionMode and drag /
        /// angularDrag are stripped and throw, which used to leave every chunk behind as a
        /// frozen cube that was never launched, evicted or destroyed. A chunk does not need
        /// continuous collision anyway - it sweeps its own path with a raycast every frame.
        /// </summary>
        private static bool Build(Vector3 p0, Vector3 forward, Color col,
                                  out GameObject go, out BoxCollider box, out Material mat, out Rigidbody rb)
        {
            go = null; box = null; mat = null; rb = null;
            try
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "FruitLib_Ejecta";
                go.layer = ChunkLayer;
                go.transform.localScale = Vector3.one * Mathf.Max(0.005f, FruitLibConfig.EjectaSize);
                go.transform.position = p0;
                box = go.GetComponent<BoxCollider>();
                if (box != null) box.enabled = false;

                // Invisible chunks still fly, stick and leave blood decals.
                var renderer = go.GetComponent<Renderer>();
                if (FruitLibConfig.EjectaVisible)
                {
                    mat = new Material(Shader.Find("Unlit/Color")) { color = col };
                    renderer.material = mat;
                }
                else renderer.enabled = false;

                rb = go.AddComponent<Rigidbody>();
                rb.mass = 0.005f;
                rb.linearDamping  = 0.3f;
                rb.angularDamping = 0.5f;
                rb.interpolation  = RigidbodyInterpolation.Interpolate;

                Vector3 spread = Random.insideUnitSphere * FruitLibConfig.EjectaSpread;
                rb.linearVelocity  = (forward + spread).normalized * FruitLibConfig.EjectaSpeed * (0.5f + (float)_rng.NextDouble());
                rb.angularVelocity = Random.onUnitSphere * Random.Range(5f, 15f);
                return true;
            }
            catch (System.Exception e)
            {
                if (_reportedBuildFailure++ == 0) MelonLogger.Warning($"{Tag} a chunk failed to build and was removed (reported once): {e}");
                if (go != null) Object.Destroy(go);
                if (mat != null) Object.Destroy(mat);
                return false;
            }
        }

        private static int _reportedBuildFailure;

        private static void Finish(LinkedListNode<Handle> node, GameObject go, Material mat)
        {
            if (node.List != null) node.List.Remove(node);
            if (go != null) Object.Destroy(go);
            if (mat != null) Object.Destroy(mat);
            if (_chunks.Count == 0) RestoreChunkCollisions();
        }

        /// <summary>Idempotent: the original is read once, before the first change.</summary>
        private static void IgnoreChunkCollisions()
        {
            if (_layerScoped) return;
            try
            {
                // Stripped in some builds; if it will not read, assume the game default (collide).
                try { _layerOriginal = Physics.GetIgnoreLayerCollision(ChunkLayer, ChunkLayer); }
                catch { _layerOriginal = false; }

                Physics.IgnoreLayerCollision(ChunkLayer, ChunkLayer, true);
                _layerScoped = true;
            }
            catch (System.Exception e)
            {
                if (!_layerWarned) { _layerWarned = true; MelonLogger.Warning($"{Tag} chunks will collide with each other: {e.Message}"); }
            }
        }

        private static void RestoreChunkCollisions()
        {
            if (!_layerScoped) return;
            _layerScoped = false;
            try { Physics.IgnoreLayerCollision(ChunkLayer, ChunkLayer, _layerOriginal); }
            catch (System.Exception e) { MelonLogger.Warning($"{Tag} could not restore the layer {ChunkLayer} collision setting: {e.Message}"); }
        }

        // ── Decals ───────────────────────────────────────────────────────────────

        private static Material _material;
        private static float    _nextAtlasLookup;
        private static bool     _atlasWarned;

        /// <summary>
        /// The decal material, rebuilt whenever it or its atlas is gone.
        ///
        /// Built once and trusted forever, it broke on the first trip through the main menu:
        /// DontDestroyOnLoad does nothing for a Material, so the game's unused-asset sweep
        /// destroyed it and no decal spawned for the rest of the session. It is now pinned
        /// with DontUnloadUnusedAsset (which keeps the atlas it references too), checked on
        /// every use, and a failed lookup - say, a wound before any level has loaded the
        /// atlas - is retried every couple of seconds instead of giving up.
        /// </summary>
        private static bool EnsureAtlas()
        {
            if (_material != null && _material.mainTexture != null) return true;
            if (Time.unscaledTime < _nextAtlasLookup) return false;
            _nextAtlasLookup = Time.unscaledTime + 2f;

            if (_material != null) Object.Destroy(_material);
            _material = null;

            Texture2D atlas = null;
            Shader shader = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>()) if (t != null && t.name == "Pixelblood") { atlas = t; break; }
            foreach (var s in Resources.FindObjectsOfTypeAll<Shader>()) if (s != null && s.name == "Sprites/Default") { shader = s; break; }

            if (atlas == null || shader == null)
            {
                if (!_atlasWarned)
                {
                    _atlasWarned = true;
                    MelonLogger.Warning($"{Tag} Pixelblood atlas or Sprites/Default shader not found yet; blood decals wait until they load");
                }
                return false;
            }

            _material = new Material(shader)
            {
                mainTexture = atlas, color = Muscle, renderQueue = 3000,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            return true;
        }

        /// <summary>A new scene may have just loaded the atlas: look again straight away.</summary>
        internal static void ResetForScene()
        {
            _nextAtlasLookup = 0f;

            // The chunks die with the scene, but their coroutines only notice a frame later:
            // do not leave the matrix changed until then. A chunk spawned in the meantime
            // switches it back on.
            RestoreChunkCollisions();
        }

        private static void Decals(Vector3 point, Vector3 normal, Transform surface)
        {
            if (!FruitLibConfig.BloodDecals || !EnsureAtlas()) return;

            Vector3 tangent = Vector3.Cross(normal, Vector3.up);
            if (tangent.sqrMagnitude < 0.01f) tangent = Vector3.Cross(normal, Vector3.forward);
            tangent.Normalize();
            Vector3 bitangent = Vector3.Cross(tangent, normal).normalized;

            float radius = FruitLibConfig.BloodDecalSize;
            int target = Mathf.RoundToInt(FruitLibConfig.BloodDecalCount * Mathf.Clamp01(1f - FruitPerfMon.PressureLevel * 2f));
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
            int cols = Mathf.Max(1, FruitLibConfig.BloodAtlasCols), rows = Mathf.Max(1, FruitLibConfig.BloodAtlasRows);
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
            float life = FruitLibConfig.BloodDecalLifetime, fade = life * 0.7f, t = 0f;
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
