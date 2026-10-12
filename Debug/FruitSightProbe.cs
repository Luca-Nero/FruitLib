using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// Spike harness for NeuroFrukt's sight phase (handover Phase B, spikes S6 and S8; S7 runs
    /// offline on the pictures this saves). Off by default; on the key it takes the body under
    /// the crosshair (Shift: every tracked body, one per few frames) and:
    ///
    /// - <b>S6 eye camera.</b> Puts a camera at the body's eyes and captures 384x256 twice: (A)
    ///   <c>Camera.Render()</c> then ReadPixels in the same frame, (B) the camera switched on for
    ///   one frame (the BombsAway ScreenFeed way) and read back the next. Times the render, the
    ///   read and a managed PNG encode, saves both PNGs, logs their mean brightness (a black
    ///   frame means that path doesn't render), and logs the frame times around each capture
    ///   against the running average, so a hitch shows as a number.
    ///
    /// - <b>S8 world names.</b> A fan of rays from the eyes and a ring around the body: what each
    ///   first hits (GameObject and parents, layer, collider type, size, rigidbody, renderer
    ///   material), a layout guess (indoors / open directions), line of sight to every other
    ///   body and to the player, and a scan of loose rigidbodies within 30 m grouped by name.
    ///   Says how much of a world can be named from what the game already calls things.
    ///
    /// While on it also shows the player's orb: a small sphere at the camera, which the player
    /// never sees (it is drawn from inside, back faces culled) but every eye capture does.
    ///
    /// Everything goes to UserData/FruitSight.log; pictures to UserData/FruitSight/.
    /// </summary>
    internal static class FruitSightProbe
    {
        private const string Tag = "[SightProbe]";
        private const int W = 384, H = 256;
        private const float HFov = 90f;
        private const float Range = 30f;

        internal static bool Enabled => FruitLibConfig.SightProbe;

        private static string Dir => Path.Combine(FruitPaths.UserData, "FruitSight");
        private static string LogPath => Path.Combine(FruitPaths.UserData, "FruitSight.log");

        private static bool _required;
        private static GameObject _camGo, _orb;
        private static Camera _cam;
        private static RenderTexture _rt;
        private static Texture2D _read;

        // The head rigidbody's local axes that point forward and up on an upright body, found once.
        private static Vector3 _headFwd, _headUp;
        private static bool _axesKnown;

        private static readonly Queue<FruitBody> _queue = new Queue<FruitBody>();
        private static FruitBody _job;
        private static int _phase, _shot, _sceneShots;
        private static float _nextJob;
        private static string _scene = "";
        private static bool _sceneLogged;

        // Frame times: a running average, and the frames right after a capture.
        private static readonly float[] _dts = new float[60];
        private static int _dtN, _dtI;
        private static readonly List<float> _after = new List<float>(4);
        private static float _avgAtCapture;

        private static StreamWriter _log;

        internal static void ResetForScene()
        {
            _queue.Clear();
            _job = null;
            _phase = 0;
            _sceneLogged = false;
            _sceneShots = 0;
            try { _scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { _scene = "scene"; }
            if (_cam != null) _cam.enabled = false;
        }

        internal static void Tick()
        {
            if (!Enabled)
            {
                if (_orb != null && _orb.activeSelf) _orb.SetActive(false);
                if (_cam != null) _cam.enabled = false;
                return;
            }
            if (!_required) { FruitBodies.Require("SightProbe"); _required = true; }

            float dt = Time.unscaledDeltaTime;
            _dts[_dtI] = dt; _dtI = (_dtI + 1) % _dts.Length; if (_dtN < _dts.Length) _dtN++;

            UpdateOrb();
            LearnHeadAxes();

            if (_job != null) { Step(dt); return; }

            if (_queue.Count > 0 && Time.unscaledTime >= _nextJob)
            {
                var b = _queue.Dequeue();
                if (b != null && b.HasPosition && b.Limbs[FruitBodies.Head].Present) Begin(b);
                return;
            }

            var key = FruitLibConfig.SightProbeKey;
            if (key == KeyCode.None || FruitMenu.IsInputSuppressed || !Input.GetKeyDown(key)) return;

            bool all = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (all)
            {
                foreach (var b in FruitBodies.All) if (b.HasPosition) _queue.Enqueue(b);
                Note($"{Tag} queued {_queue.Count} bodies");
            }
            else
            {
                var b = LookedAt();
                if (b == null) { Note($"{Tag} no tracked body under the crosshair (Shift + key takes all of them)"); return; }
                _queue.Enqueue(b);
            }
        }

        private static FruitBody LookedAt()
        {
            if (!FruitBodies.HasView) return null;
            FruitBody best = null;
            float bestA = 12f;
            foreach (var b in FruitBodies.All)
            {
                if (!b.HasPosition) continue;
                float a = Vector3.Angle(FruitBodies.ViewForward, b.HeadPosition - FruitBodies.ViewPosition);
                if (a < bestA) { bestA = a; best = b; }
            }
            return best;
        }

        // ── The capture, over three-plus frames ──

        private static void Begin(FruitBody b)
        {
            _job = b;
            _shot++;
            _sceneShots++;
            _phase = 0;
            if (!_sceneLogged) { _sceneLogged = true; LogScene(); }
        }

        private static void Step(float dt)
        {
            var b = _job;
            string stem = Path.Combine(Dir, $"{Safe(_scene)}_{_shot:000}_{Safe(b.Name)}");
            try
            {
                switch (_phase)
                {
                    case 0:
                    {
                        if (!EnsureCamera()) { _job = null; return; }
                        Directory.CreateDirectory(Dir);
                        var sb = new StringBuilder(2048);
                        sb.Append($"\n==== #{_shot} {b.Name} (body#{b.Id}) in {_scene} at {Time.time:0.0}s: {b.BehaviourText()}, {b.PostureText()}\n");
                        Aim(b, sb);
                        _avgAtCapture = Average();

                        // (A) Render() and read in the same frame.
                        var sw = Stopwatch.StartNew();
                        _cam.Render();
                        double render = sw.Elapsed.TotalMilliseconds;
                        sw.Restart();
                        byte[] rgba = ReadBack(out double readMs);
                        double total = sw.Elapsed.TotalMilliseconds;
                        sw.Restart();
                        byte[] png = FruitPng.FromRgba(rgba, W, H);
                        double enc = sw.Elapsed.TotalMilliseconds;
                        File.WriteAllBytes(stem + "_A.png", png);
                        sb.Append($"  S6 A Render(): render {render:0.00} ms, ReadPixels {readMs:0.00} ms (+copy {total - readMs:0.00}), PNG {enc:0.00} ms, {png.Length / 1024} KB, mean brightness {Mean(rgba):0}/255 -> {Path.GetFileName(stem)}_A.png\n");
                        Write(sb.ToString());

                        // (B) switched on for this frame; read next frame.
                        Aim(b, null);
                        _cam.enabled = true;
                        _phase = 1;
                        _after.Clear();
                        return;
                    }
                    case 1:
                    {
                        _cam.enabled = false;
                        _after.Add(dt);
                        var sw = Stopwatch.StartNew();
                        byte[] rgba = ReadBack(out double readMs);
                        double total = sw.Elapsed.TotalMilliseconds;
                        sw.Restart();
                        byte[] png = FruitPng.FromRgba(rgba, W, H);
                        double enc = sw.Elapsed.TotalMilliseconds;
                        File.WriteAllBytes(stem + "_B.png", png);
                        var sb = new StringBuilder(4096);
                        sb.Append($"  S6 B enabled for a frame: ReadPixels {readMs:0.00} ms (+copy {total - readMs:0.00}), PNG {enc:0.00} ms, {png.Length / 1024} KB, mean brightness {Mean(rgba):0}/255 -> {Path.GetFileName(stem)}_B.png\n");

                        sw.Restart();
                        Survey(b, sb);
                        sb.Append($"  S8 survey took {sw.Elapsed.TotalMilliseconds:0.0} ms in all\n");
                        Write(sb.ToString());
                        _phase = 2;
                        return;
                    }
                    default:
                    {
                        _after.Add(dt);
                        if (_after.Count < 4) return;
                        var sb = new StringBuilder("  S6 frame times around the capture (ms):");
                        foreach (var a in _after) sb.Append($" {a * 1000f:0.0}");
                        sb.Append($" vs average {_avgAtCapture * 1000f:0.0} (the first is the frame with the (A) capture and the (B) render, the second has the (B) read and the survey)\n");
                        Write(sb.ToString());
                        Note($"{Tag} #{_shot} {b.Name}: saved {Path.GetFileName(stem)}_A/_B.png, details in FruitSight.log");
                        _job = null;
                        _nextJob = Time.unscaledTime + 0.25f;
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Write($"  FAILED in phase {_phase}: {e}\n");
                MelonLogger.Warning($"{Tag} capture of {b.Name} failed in phase {_phase}: {e.Message} (details in FruitSight.log)");
                if (_cam != null) _cam.enabled = false;
                _job = null;
            }
        }

        private static bool EnsureCamera()
        {
            if (_rt == null)
            {
                _rt = new RenderTexture(W, H, 24) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                _rt.Create();
            }
            if (_read == null) _read = new Texture2D(W, H, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (_cam == null)
            {
                if (_camGo != null) Object.Destroy(_camGo);
                _camGo = new GameObject("FruitSight_EyeCamera");
                Object.DontDestroyOnLoad(_camGo);
                _cam = _camGo.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.targetTexture = _rt;
                // Unity's field of view is vertical: 90 degrees across a 3:2 frame.
                _cam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(HFov * 0.5f * Mathf.Deg2Rad) * H / W) * Mathf.Rad2Deg;
                _cam.nearClipPlane = 0.05f;
                try { _cam.farClipPlane = 400f; } catch { }
                try { Write($"  eye camera made: cullingMask 0x{_cam.cullingMask:X8}, clearFlags {_cam.clearFlags}, main camera near {Camera.main?.nearClipPlane:0.000}\n"); } catch { }
            }
            return _cam != null;
        }

        /// <summary>Places the eye camera at the body's eyes; notes how in <paramref name="sb"/>.</summary>
        private static void Aim(FruitBody b, StringBuilder sb)
        {
            Eye(b, out Vector3 eye, out Vector3 fwd, out Vector3 up, out string how);
            _camGo.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(fwd, up));
            if (sb != null)
                sb.Append($"  eye at {eye.x:0.00},{eye.y:0.00},{eye.z:0.00} looking {fwd.x:0.00},{fwd.y:0.00},{fwd.z:0.00} ({how}); pitch {-Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg:0} deg\n");
        }

        private static void Eye(FruitBody b, out Vector3 eye, out Vector3 fwd, out Vector3 up, out string how)
        {
            var rb = HeadRb(b);
            Vector3 head = b.LiveHeadPosition;
            if (rb != null && _axesKnown)
            {
                Quaternion q = rb.rotation;
                fwd = (q * _headFwd).normalized;
                up = (q * _headUp).normalized;
                how = "head rigidbody";
            }
            else
            {
                fwd = b.Facing; up = Vector3.up;
                how = rb == null ? "no head rigidbody, pelvis facing" : "head axes not learnt yet (no upright body seen), pelvis facing";
            }
            eye = head + fwd * 0.11f + up * 0.04f;
        }

        private static Rigidbody HeadRb(FruitBody b)
        {
            var l = b.Limbs[FruitBodies.Head];
            if (!l.Present || l.Physics == null || l.Physics.WasCollected) return null;
            try { return l.Physics.m_rb; } catch { return null; }
        }

        /// <summary>On an upright, standing body the head's forward is the pelvis facing and its up
        /// is world up: whichever local axes of the head rigidbody line up with those are the eyes'.</summary>
        private static void LearnHeadAxes()
        {
            if (_axesKnown) return;
            foreach (var b in FruitBodies.All)
            {
                if (!b.Alive || b.Posture != FruitPosture.Upright || b.Behaviour != FruitBehaviour.Standing) continue;
                var rb = HeadRb(b);
                if (rb == null) continue;
                Quaternion q = rb.rotation;
                Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
                string[] names = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
                int bf = 0, bu = 0; float df = -2f, du = -2f;
                var sb = new StringBuilder();
                for (int i = 0; i < 6; i++)
                {
                    Vector3 w = q * axes[i];
                    float f = Vector3.Dot(w, b.Facing), u = Vector3.Dot(w, Vector3.up);
                    if (f > df) { df = f; bf = i; }
                    if (u > du) { du = u; bu = i; }
                    sb.Append($" {names[i]}:fwd {f:0.00}/up {u:0.00}");
                }
                if (df < 0.7f || du < 0.7f || bf / 2 == bu / 2) continue;   // a nodding or tilted head: try again later
                _headFwd = axes[bf]; _headUp = axes[bu];
                _axesKnown = true;
                Write($"\nhead axes learnt from {b.Name}: forward {names[bf]} ({df:0.00}), up {names[bu]} ({du:0.00}).{sb}\n");
                Note($"{Tag} head axes: forward {names[bf]}, up {names[bu]}");
                return;
            }
        }

        private static byte[] ReadBack(out double readMs)
        {
            var prev = RenderTexture.active;
            var sw = Stopwatch.StartNew();
            try
            {
                RenderTexture.active = _rt;
                _read.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
                readMs = sw.Elapsed.TotalMilliseconds;
            }
            finally { RenderTexture.active = prev; }
            Il2CppStructArray<Color32> px = _read.GetPixels32();
            var bytes = new byte[W * H * 4];
            // One block copy out of the IL2CPP array: its indexer is three native calls a texel.
            Marshal.Copy(px.Pointer + 4 * IntPtr.Size, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float Mean(byte[] rgba)
        {
            long s = 0;
            for (int i = 0; i < rgba.Length; i += 4) s += rgba[i] + rgba[i + 1] + rgba[i + 2];
            return s / (rgba.Length / 4 * 3f);
        }

        // ── The orb ──

        private static void UpdateOrb()
        {
            if (!FruitBodies.HasView) return;
            if (_orb == null)
            {
                try
                {
                    _orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    _orb.name = "FruitSight_PlayerOrb";
                    Object.DontDestroyOnLoad(_orb);
                    var col = _orb.GetComponent<Collider>();
                    if (col != null) Object.Destroy(col);
                    _orb.transform.localScale = Vector3.one * 0.3f;
                    var r = _orb.GetComponent<Renderer>();
                    var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                    if (r != null && sh != null)
                    {
                        r.material = new Material(sh) { color = new Color(0.55f, 0.75f, 1f) };
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.receiveShadows = false;
                    }
                    Write($"orb made (shader {sh?.name ?? "none"})\n");
                }
                catch (Exception e) { Write($"orb failed: {e.Message}\n"); _orb = null; return; }
            }
            if (!_orb.activeSelf) _orb.SetActive(true);
            _orb.transform.position = FruitBodies.ViewPosition;
        }

        // ── S8: what the world calls things ──

        private struct Hit { public float D; public Collider C; public FruitBody Body; public int Limb; }

        private static readonly List<Hit> _hits = new List<Hit>(16);

        /// <summary>The nearest hit along a ray that isn't <paramref name="self"/> (or the orb), or false.</summary>
        private static bool First(Vector3 from, Vector3 dir, float dist, FruitBody self, out Hit hit)
        {
            hit = default;
            var all = Physics.RaycastAll(from, dir, dist, ~0, QueryTriggerInteraction.Ignore);
            _hits.Clear();
            for (int i = 0; i < all.Length; i++)
            {
                var h = all[i];
                var c = h.collider;
                if (c == null) continue;
                var body = FruitBodies.OfObject(c.gameObject, out int limb);
                if (body == self) continue;
                _hits.Add(new Hit { D = h.distance, C = c, Body = body, Limb = limb });
            }
            if (_hits.Count == 0) return false;
            _hits.Sort((a, b) => a.D.CompareTo(b.D));
            hit = _hits[0];
            return true;
        }

        private static readonly Dictionary<IntPtr, string> _described = new Dictionary<IntPtr, string>();

        private static string Describe(Hit h)
        {
            if (h.Body != null) return $"body#{h.Body.Id} {h.Body.Name} {(h.Limb >= 0 ? FruitBodies.LimbNames[h.Limb] : "?")}";
            IntPtr key = h.C.Pointer;
            if (_described.TryGetValue(key, out var s)) return s;
            var sb = new StringBuilder();
            var go = h.C.gameObject;
            sb.Append('"').Append(go.name).Append('"');
            var t = go.transform.parent;
            for (int i = 0; t != null && i < 4; i++, t = t.parent) sb.Append(" < ").Append(t.name);
            if (t != null) sb.Append(" < ...");
            sb.Append($" [layer {go.layer}, {h.C.GetIl2CppType().Name}");
            try { var sz = h.C.bounds.size; sb.Append($", {sz.x:0.0}x{sz.y:0.0}x{sz.z:0.0} m"); } catch { }
            try
            {
                var rb = h.C.attachedRigidbody;
                if (rb != null) sb.Append($", rb {rb.mass:0.#} kg{(rb.isKinematic ? " kinematic" : "")}");
                else sb.Append(", static");
            }
            catch { }
            try
            {
                var r = go.GetComponent<Renderer>() ?? go.GetComponentInParent<Renderer>();
                var m = r != null ? r.sharedMaterial : null;
                if (m != null) sb.Append($", mat \"{m.name}\"");
            }
            catch { }
            sb.Append(']');
            s = sb.ToString();
            _described[key] = s;
            return s;
        }

        private static void Survey(FruitBody self, StringBuilder sb)
        {
            Eye(self, out Vector3 eye, out Vector3 fwd, out _, out _);
            Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
            if (flat.sqrMagnitude < 0.01f) flat = self.Facing;
            flat.Normalize();
            var sw = Stopwatch.StartNew();
            int rays = 0;

            // The fan: 13 bearings across the 90 degree view, four heights.
            sb.Append("  S8 view fan (bearing / elevation: distance, first thing hit):\n");
            var counts = new Dictionary<string, int>();
            foreach (float elev in new[] { 25f, 0f, -15f, -40f })
            {
                for (int i = -6; i <= 6; i++)
                {
                    float yaw = i * 7.5f;
                    Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(-elev, Vector3.Cross(Vector3.up, flat)) * flat;
                    rays++;
                    bool hit = First(eye, dir, Range, self, out var h);
                    string thing = hit ? Describe(h) : "nothing within 30 m";
                    if (i % 3 == 0) sb.Append($"    {yaw,4:0} / {elev,3:0}: {(hit ? $"{h.D:0.0} m " : "")}{thing}\n");
                    counts[thing] = counts.TryGetValue(thing, out int n) ? n + 1 : 1;
                }
            }
            sb.Append("  distinct things in the fan (rays):\n");
            foreach (var kv in counts) sb.Append($"    {kv.Value,2}x {kv.Key}\n");

            // The ring: 12 bearings level at eye height, one up, one down.
            int walls = 0;
            var ring = new StringBuilder();
            Vector3 eyeLevel = new Vector3(self.Position.x, self.Position.y + 1.5f, self.Position.z);
            if (self.Posture == FruitPosture.Upright) eyeLevel = eye;
            for (int i = 0; i < 12; i++)
            {
                Vector3 dir = Quaternion.AngleAxis(i * 30f, Vector3.up) * flat;
                rays++;
                bool hit = First(eyeLevel, dir, 25f, self, out var h);
                if (hit && h.Body == null && h.D < 12f) walls++;
                ring.Append(hit ? $" {i * 30}:{h.D:0.0}" : $" {i * 30}:open");
            }
            rays += 2;
            string upS = First(eyeLevel, Vector3.up, 25f, self, out var hu) ? $"{hu.D:0.0} m {Describe(hu)}" : "sky";
            string downS = First(eyeLevel, Vector3.down, 5f, self, out var hd) ? $"{hd.D:0.0} m {Describe(hd)}" : "nothing";
            sb.Append($"  S8 ring (deg from facing: m):{ring}\n  up: {upS}\n  down: {downS}\n");
            sb.Append($"  layout guess: {(upS != "sky" && walls >= 8 ? "INDOORS" : walls >= 8 ? "enclosed, open above" : "outdoors")}, walls within 12 m on {walls}/12 bearings\n");

            // Line of sight to the other bodies (head, torso, pelvis) and to the player.
            sb.Append("  S8 line of sight:\n");
            foreach (var o in FruitBodies.All)
            {
                if (o == self || !o.HasPosition || Vector3.Distance(o.Position, self.Position) > 40f) continue;
                Vector3[] pts = { o.LiveHeadPosition, o.Limbs[FruitBodies.Torso].LivePosition, o.Limbs[FruitBodies.Pelvis].LivePosition };
                string[] pn = { "head", "torso", "pelvis" };
                var line = new StringBuilder();
                bool any = false;
                for (int k = 0; k < 3; k++)
                {
                    Vector3 d = pts[k] - eye;
                    rays++;
                    bool seen = !First(eye, d.normalized, d.magnitude + 0.3f, self, out var h) || h.Body == o;
                    any |= seen;
                    line.Append($" {pn[k]} {(seen ? "seen" : "blocked by " + Describe(h))};");
                }
                float ang = Vector3.Angle(fwd, o.LiveHeadPosition - eye);
                sb.Append($"    {o.Name} {Vector3.Distance(o.Position, self.Position):0.0} m, {ang:0} deg off gaze ({(ang <= 60f ? "in view" : ang <= 100f ? "corner of the eye" : "behind")}): {(any ? "VISIBLE" : "hidden")} -{line}\n");
            }
            if (FruitBodies.HasView)
            {
                Vector3 d = FruitBodies.ViewPosition - eye;
                rays++;
                bool clear = !First(eye, d.normalized, d.magnitude - 0.2f, self, out var h);
                sb.Append($"    the player (orb) {d.magnitude:0.0} m, {Vector3.Angle(fwd, d):0} deg off gaze: {(clear ? "VISIBLE" : "blocked by " + Describe(h))}\n");
            }
            sb.Append($"  ({rays} rays in {sw.Elapsed.TotalMilliseconds:0.0} ms)\n");

            // Loose rigidbodies within 30 m, grouped by name.
            sw.Restart();
            Il2CppArrayBase<Rigidbody> rbs = null;
            try { rbs = Object.FindObjectsOfType<Rigidbody>(false); } catch (Exception e) { sb.Append($"  rigidbody scan threw: {e.Message}\n"); }
            double scanMs = sw.Elapsed.TotalMilliseconds;
            if (rbs != null)
            {
                var groups = new Dictionary<string, (int n, float mass, float near, string size)>();
                int inBodies = 0, far = 0;
                foreach (var rb in rbs)
                {
                    if (rb == null) continue;
                    var go = rb.gameObject;
                    float d = Vector3.Distance(rb.position, self.Position);
                    if (d > Range) { far++; continue; }
                    if (FruitBodies.OfObject(go, out _) != null) { inBodies++; continue; }
                    string name = go.name;
                    var root = go.transform.root;
                    if (root != null && root != go.transform) name += " < " + root.name;
                    string size = "";
                    try { var r = go.GetComponentInChildren<Renderer>(); if (r != null) { var s = r.bounds.size; size = $"{s.x:0.0}x{s.y:0.0}x{s.z:0.0} m"; } } catch { }
                    groups[name] = groups.TryGetValue(name, out var g)
                        ? (g.n + 1, g.mass, Mathf.Min(g.near, d), g.size)
                        : (1, rb.mass, d, size);
                }
                sb.Append($"  S8 rigidbodies: {rbs.Length} in the scene (FindObjectsOfType {scanMs:0.0} ms), {inBodies} in bodies, {far} past 30 m, {groups.Count} kinds near:\n");
                int shown = 0;
                foreach (var kv in groups)
                {
                    if (shown++ >= 40) { sb.Append($"    ... {groups.Count - 40} more kinds\n"); break; }
                    sb.Append($"    {kv.Value.n,3}x \"{kv.Key}\" {kv.Value.mass:0.##} kg {kv.Value.size} nearest {kv.Value.near:0.0} m\n");
                }
            }
        }

        /// <summary>Once per scene: the scene's root objects and how many colliders there are, by root.</summary>
        private static void LogScene()
        {
            var sb = new StringBuilder($"\n######## scene {_scene}\n");
            try
            {
                var sw = Stopwatch.StartNew();
                var cols = Object.FindObjectsOfType<Collider>(false);
                double ms = sw.Elapsed.TotalMilliseconds;
                var byRoot = new Dictionary<string, int>();
                foreach (var c in cols)
                {
                    if (c == null) continue;
                    string r = c.transform.root.name;
                    byRoot[r] = byRoot.TryGetValue(r, out int n) ? n + 1 : 1;
                }
                sb.Append($"colliders: {cols.Length} (FindObjectsOfType {ms:0.0} ms), by root object:\n");
                var list = new List<KeyValuePair<string, int>>(byRoot);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < list.Count && i < 50; i++) sb.Append($"  {list[i].Value,5} {list[i].Key}\n");
                if (list.Count > 50) sb.Append($"  ... {list.Count - 50} more roots\n");
            }
            catch (Exception e) { sb.Append($"collider scan threw: {e.Message}\n"); }
            Write(sb.ToString());
        }

        // ── Bits ──

        private static float Average()
        {
            if (_dtN == 0) return 0f;
            float s = 0f;
            for (int i = 0; i < _dtN; i++) s += _dts[i];
            return s / _dtN;
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private static void Write(string s)
        {
            try
            {
                if (_log == null)
                {
                    _log = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
                    _log.WriteLine($"# FruitSight probe, FruitLib {FruitVersion.Current}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {Screen.width}x{Screen.height}, {SystemInfo.graphicsDeviceName}");
                }
                _log.Write(s);
            }
            catch { }
        }

        private static void Note(string s) => MelonLogger.Msg(s);
    }
}
