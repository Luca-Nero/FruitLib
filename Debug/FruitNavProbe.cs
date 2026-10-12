using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// Test harness for NeuroFrukt's pathing (handover Phase C). Off by default; while on,
    /// FruitNav runs and is drawn (as with NavDraw), every goto order is measured
    /// (FruitBodyControl.Telemetry.cs) and logged when it ends, and the key does:
    ///
    /// - <b>Key:</b> rebuilds the navmesh now and logs, for every tracked body, the path to the
    ///   point under the crosshair: status, corners, length, the tightest spot.
    /// - <b>Shift + key:</b> sends every body there (FruitBodyControl.GoTo) and logs how each
    ///   order ends, with its walking measurements.
    /// - <b>Ctrl + key:</b> the walking course (5.21.0). The first press builds it on open ground at
    ///   the crosshair, leading away from you and to the right: corridors 1.8, 1.4 (with a 1 m
    ///   door), 1.1 and 1.0 m wide with 90 and 45 degree bends, ledges of 2, 4, 7 and 12 cm across
    ///   them (5.24.0), into a room with a hairpin chicane. Each press then gathers every body at
    ///   the start and sends them through one at a time, a few seconds apart, half with the 5.23
    ///   footing and half with the 5.24 footing (swapped each run; 5.21-5.23 compared 5.20 and
    ///   5.21 walking), and logs a trace of each and a table comparing the two.
    ///   Ctrl+Shift removes it.
    /// - <b>Alt + key:</b> spike S3, the stair and ramp climb, in front of the body under the
    ///   crosshair (next variant each press; Alt+Shift removes it).
    ///
    /// Everything goes to UserData/FruitNav.log.
    /// </summary>
    internal static class FruitNavProbe
    {
        private const string Tag = "[NavProbe]";

        internal static bool Enabled => FruitLibConfig.NavProbe;

        private static string LogPath => Path.Combine(FruitPaths.UserData, "FruitNav.log");

        private const int NavLayers = FruitNav.Layers;
        private const int EnvLayer = 8;

        private static bool _hooked;
        private static string _scene = "";
        private static StreamWriter _log;
        private static readonly Dictionary<int, float> _sent = new Dictionary<int, float>();

        internal static void ResetForScene()
        {
            _sent.Clear();
            RemoveClimb();
            RemoveTrack();
            try { _scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { _scene = "scene"; }
        }

        internal static void Tick()
        {
            if (!Enabled)
            {
                if (_climb != null) RemoveClimb();
                if (_track != null) RemoveTrack();
                return;
            }
            if (!_hooked)
            {
                _hooked = true;
                FruitBodyControl.MoveEnded += OnMoveEnded;
                FruitBodyControl.Stepped += OnStepped;
                FruitBodyControl.Sampled += OnSampled;
                FruitBodyControl.Summarised += OnSummarised;
                FruitNav.Rebuilt += () => { if (Enabled) Write($"  [{Time.time:0.0}] {FruitNav.LastBuild}\n"); };
            }

            TickClimb();
            TickTrack();

            var key = FruitLibConfig.NavProbeKey;
            if (key == KeyCode.None || FruitMenu.IsInputSuppressed || !Input.GetKeyDown(key)) return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (alt && shift) { RemoveClimb(); Note($"{Tag} climb removed"); }
            else if (alt) StartClimb();
            else if (ctrl && shift) { RemoveTrack(); Note($"{Tag} course removed"); }
            else if (ctrl) TrackKey();
            else if (shift) SendAll();
            else Report();
        }

        // ── Paths ──

        private static bool Aim(out Vector3 point, out string on)
        {
            point = default; on = "";
            if (!FruitBodies.HasView) return false;
            if (!Physics.Raycast(FruitBodies.ViewPosition, FruitBodies.ViewForward, out RaycastHit aim, 300f, NavLayers, QueryTriggerInteraction.Ignore))
                return false;
            point = aim.point;
            on = aim.collider != null ? aim.collider.name : "";
            return true;
        }

        private static void Report()
        {
            FruitNav.MarkDirty(0f);
            var sb = new StringBuilder();
            sb.Append($"\n===== paths, {_scene}, {DateTime.Now:HH:mm:ss}, FruitLib {FruitVersion.Current} =====\n");
            sb.Append($"  navmesh: {(FruitNav.Ready ? FruitNav.LastBuild : "none yet")}; rebuilding now; step assist {(FruitLibConfig.StepAssist ? "on" : "off")}; " +
                      $"path margin {FruitNav.PathMargin:0.00} m, smooth walking {(FruitLibConfig.NavSmoothWalking ? "on" : "off")}\n");
            if (!FruitNav.Ready) { Write(sb.ToString()); Note($"{Tag} no navmesh yet; press again in a moment"); return; }
            if (!Aim(out Vector3 goal, out string on)) { sb.Append("  nothing under the crosshair\n"); Write(sb.ToString()); return; }
            sb.Append($"  to ({goal.x:0.0}, {goal.y:0.0}, {goal.z:0.0}) on \"{on}\":\n");

            var path = new FruitPath();
            foreach (var b in FruitBodies.All)
            {
                if (!b.Alive || !b.HasPosition) continue;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = FruitNav.FindPath(b.Position, goal, path, 3f, true);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (!ok) { sb.Append($"    #{b.Id} {b.Name}: no path ({ms:0.00} ms)\n"); continue; }
                float room = path.RoomAhead(0f, path.Length);
                sb.Append($"    #{b.Id} {b.Name}: {path.Status}, {path.Corners.Count} corners, {path.Length:0.0} m, " +
                          $"tightest {(float.IsInfinity(room) ? "-" : room.ToString("0.00"))} m spare, {ms:0.00} ms");
                if (FruitNav.FindPath(b.Position, goal, path, 3f, true, true) && path.Shaped)
                {
                    room = path.RoomAhead(0f, path.Length);
                    sb.Append($"; shaped {path.Length:0.0} m, tightest {(float.IsInfinity(room) ? "-" : room.ToString("0.00"))}, {FruitNav.LastShapeMs:0.00} ms");
                }
                else sb.Append("; shaping kept it plain");
                sb.Append('\n');
            }
            Write(sb.ToString());
            Note($"{Tag} paths logged; Shift+{FruitLibConfig.NavProbeKey} sends the bodies");
        }

        private static void SendAll()
        {
            if (!Aim(out Vector3 goal, out string on)) { Note($"{Tag} nothing under the crosshair"); return; }
            var sb = new StringBuilder($"\n----- sending bodies to ({goal.x:0.0}, {goal.y:0.0}, {goal.z:0.0}) on \"{on}\", {DateTime.Now:HH:mm:ss} -----\n");
            int n = 0;
            foreach (var b in FruitBodies.All)
            {
                if (!b.Alive || !b.HasPosition) continue;
                if (FruitBodyControl.GoTo(b, goal, 0.9f, 90f)) { _sent[b.Id] = Time.time; n++; }
                var p = FruitBodyControl.PathOf(b);
                sb.Append($"  #{b.Id} {b.Name} sets off{(p != null ? $" ({p.Status}, {p.Length:0.0} m{(p.Shaped ? ", shaped" : "")})" : " (straight until its path is found)")}\n");
            }
            Write(sb.ToString());
            Note($"{Tag} sent {n} bodies");
        }

        private static void OnMoveEnded(FruitBody b, FruitMoveResult r)
        {
            if (!Enabled || b == null) return;
            if (_track != null) TrackMoveEnded(b, r);
            if (!_sent.TryGetValue(b.Id, out float t0)) return;
            _sent.Remove(b.Id);
            Vector3 p = b.Limbs[FruitBodies.Pelvis].Present ? b.Limbs[FruitBodies.Pelvis].LivePosition : b.Position;
            Write($"  #{b.Id} {b.Name}: {r.ToString().ToUpperInvariant()} after {Time.time - t0:0.0} s at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0})\n");
        }

        private static void OnStepped(FruitBody b, float h, int foot)
        {
            if (!Enabled || b == null) return;
            string line = $"  [{Time.time:0.00}] #{b.Id} step assist: {h * 100f:0} cm, {(foot == 3 ? "left" : "right")} foot\n";
            if (_track != null && _track.Running.TryGetValue(b.Id, out var e) && e.Phase == Leg.Course) e.Lifts++;
            if (_climb != null && _climb.Walking && _climb.Body == b) { _climb.Steps++; _climb.Trace.Append(line); }
            else Write(line);
        }

        // ── Walking measurements ──

        private static readonly Dictionary<int, FruitWalkSummary> _summaries = new Dictionary<int, FruitWalkSummary>();

        private static void OnSampled(FruitBody b, FruitWalkSample s)
        {
            if (!Enabled || b == null) return;
            // Traced: bodies on the course, and bodies sent with Shift.
            bool onCourse = _track != null && _track.Running.TryGetValue(b.Id, out var e) && e.Phase == Leg.Course;
            if (!onCourse && !_sent.ContainsKey(b.Id)) return;
            Write($"  #{b.Id,-2} {Way(s.Legacy, s.OldFooting)} {s.T,5:0.0}s along {s.Along,5:0.0} left {s.Left,5:0.0}  off {s.Off,6:+0.00;-0.00}  sway {s.Sway,6:+0.00;-0.00}  " +
                  $"v {s.Speed,4:0.00}  head {s.HeadingError,4:0}  turn {s.TurnAhead,3:0}  yaw/s {s.YawRate,4:0}  pace {s.Pace:0.00}  " +
                  $"room {(float.IsInfinity(s.Room) ? "  - " : s.Room.ToString("0.00"))}  wall {s.Wall:0.00}" +
                  $"{(s.Tight ? " T" : "")}{(s.Pivot ? " PIVOT" : "")}{(s.Recovering ? " BACKOFF" : "")}{(s.Yielding ? " WAIT" : "")}{(!s.Upright ? " " + s.Behaviour.ToString().ToUpperInvariant() : "")}\n");
        }

        private static void OnSummarised(FruitBody b, FruitWalkSummary s)
        {
            if (!Enabled || b == null) return;
            _summaries[b.Id] = s;
            bool course = _track != null && _track.Running.TryGetValue(b.Id, out var e) && e.Phase != Leg.Done;
            if (course) return;   // the course writes its own table
            Write($"  [{Time.time:0.0}] #{b.Id} {b.Name} walk ({Way(s.Legacy, s.OldFooting)}): {Line(s)}\n");
        }

        private static string Way(bool legacy, bool oldFeet) => legacy ? "5.20" : oldFeet ? "old feet" : "new feet";

        private static string Line(FruitWalkSummary s) =>
            $"{s.Result.ToString().ToUpperInvariant()} in {s.Seconds:0.0} s, path {s.PathLength:0.0} m{(s.Shaped ? " shaped" : "")}, walked {s.Walked:0.0} m at {s.MeanSpeed:0.00} m/s, " +
            $"off rms {s.OffRms:0.00} max {s.OffMax:0.00}, sway rms {s.SwayRms:0.00} max {s.SwayMax:0.00}, nearest wall {s.WallMin:0.00} m, " +
            $"{s.Contacts} wall contacts, {s.Falls} falls, {s.Retries} back-offs, {s.Pivots} pivots, {s.Reslots} spot changes, {s.Yields} waits, turns up to {s.YawRateMax:0} deg/s, shaping {s.ShapeMs:0.0} ms";

        // ── The walking course ──

        private enum Leg { Building, Gathering, Waiting, Course, Done }

        private sealed class Entry
        {
            public FruitBody Body;
            public Leg Phase;
            public bool OldFeet;
            public int Lifts;
            public float LaunchAt;
            public bool Interrupted;
            public FruitWalkSummary Summary;
        }

        private sealed class Track
        {
            public GameObject Root;
            public Vector3 O, F, R;
            public Quaternion Rot;
            public float GroundY;
            public int Runs;
            public bool Active;
            public Leg Phase;
            public int BuiltVersion;
            public float PhaseAt;
            public readonly Dictionary<int, Entry> Running = new Dictionary<int, Entry>();
            public readonly List<Entry> Order = new List<Entry>();
        }

        private static Track _track;

        // Course plan, metres, x to the right and z ahead of where it starts.
        private static readonly Vector2[] Centre = { new Vector2(0f, 0f), new Vector2(0f, 7f), new Vector2(6f, 7f), new Vector2(6f, 12f), new Vector2(9f, 15f), new Vector2(9f, 19f) };
        private static readonly float[] Widths = { 1.8f, 1.4f, 1.1f, 1.1f, 1.0f };
        // A 1 m door is what FruitCity has; the navmesh keeps 0.2 m of it (4 cells off each jamb).
        private const float WallH = 1.8f, WallT = 0.2f, DoorGap = 1.0f;
        private const float RoomX0 = 5f, RoomX1 = 13f, RoomZ0 = 19f, RoomZ1 = 29f, ChicaneGap = 1.6f;
        private static readonly Vector2 GoalAt = new Vector2(9f, 27.6f);
        // Ledges across the corridors: (x, z) of the middle, w = height. Under z 6 they span the
        // first corridor (1.8 m), else the last (1.0 m). Each is LedgeDepth deep: a step up, then down.
        private static readonly Vector4[] Ledges = { new Vector4(0f, 1.6f, 0f, 0.02f), new Vector4(0f, 3.2f, 0f, 0.04f), new Vector4(0f, 4.8f, 0f, 0.07f), new Vector4(9f, 16.6f, 0f, 0.12f) };
        private const float LedgeDepth = 0.6f;
        private const float Stagger = 7f, GatherSeconds = 45f, CourseSeconds = 120f;

        private static Vector3 W(Track t, float x, float z)
        {
            Vector3 p = t.O;
            p.x += t.R.x * x + t.F.x * z;
            p.z += t.R.z * x + t.F.z * z;
            return p;
        }

        private static void TrackKey()
        {
            if (_track == null) { BuildTrack(); return; }
            if (_track.Active) { Note($"{Tag} a run is under way (Ctrl+Shift+{FruitLibConfig.NavProbeKey} removes the course)"); return; }
            StartRun();
        }

        private static void BuildTrack()
        {
            if (!Aim(out Vector3 at, out _)) { Note($"{Tag} aim at open ground to put the course there"); return; }
            Vector3 f = FruitBodies.ViewForward; f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();
            var t = new Track { O = at, F = f, R = new Vector3(f.z, 0f, -f.x), GroundY = at.y };
            t.Rot = Quaternion.LookRotation(f, Vector3.up);

            // Room for it: the ground under its footprint has to be level and clear.
            string blocked = Footprint(t);
            if (blocked != null) { Note($"{Tag} no room for the course here: {blocked}. It needs about 15 x 36 m of open, level ground ahead and to the right"); return; }

            t.Root = new GameObject("FruitLib_NavCourse");
            t.Root.transform.position = t.O;
            t.Root.transform.rotation = t.Rot;
            var wall = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color")) { color = new Color(0.62f, 0.62f, 0.66f) };
            var mark = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color")) { color = new Color(0.95f, 0.75f, 0.1f) };

            // Corridor walls, both sides: the offset lines of each leg, joined where they meet.
            for (int side = -1; side <= 1; side += 2)
            {
                var pts = new List<Vector2>();
                int n = Centre.Length;
                for (int i = 0; i < n; i++)
                {
                    if (i == 0) { pts.Add(Offset(0, side, true)); continue; }
                    if (i == n - 1) { pts.Add(Offset(n - 2, side, false)); continue; }
                    pts.Add(Join(i - 1, i, side));
                }
                for (int i = 1; i < pts.Count; i++) WallPiece(t.Root, wall, pts[i - 1], pts[i]);
            }

            // A door in the 1.4 m corridor: two jambs leaving 0.9 m.
            float jamb = (Widths[1] - DoorGap) * 0.5f;
            Box(t.Root, mark, new Vector3(3f, WallH * 0.5f, 7f + Widths[1] * 0.5f - jamb * 0.5f), new Vector3(0.2f, WallH, jamb), Quaternion.identity);
            Box(t.Root, mark, new Vector3(3f, WallH * 0.5f, 7f - Widths[1] * 0.5f + jamb * 0.5f), new Vector3(0.2f, WallH, jamb), Quaternion.identity);

            // The room, entered only from the corridor, with a chicane of two walls with gaps at alternate ends.
            // (A piece runs half a wall's thickness past each end, so ends at an opening are pulled in by that.)
            float cx = Centre[Centre.Length - 1].x, half = Widths[Widths.Length - 1] * 0.5f, ht = WallT * 0.5f;
            WallPiece(t.Root, wall, new Vector2(RoomX0, RoomZ0), new Vector2(cx - half - ht, RoomZ0));
            WallPiece(t.Root, wall, new Vector2(cx + half + ht, RoomZ0), new Vector2(RoomX1, RoomZ0));
            WallPiece(t.Root, wall, new Vector2(RoomX0, RoomZ1), new Vector2(RoomX1, RoomZ1));
            WallPiece(t.Root, wall, new Vector2(RoomX0, RoomZ0), new Vector2(RoomX0, RoomZ1));
            WallPiece(t.Root, wall, new Vector2(RoomX1, RoomZ0), new Vector2(RoomX1, RoomZ1));
            WallPiece(t.Root, mark, new Vector2(RoomX0, 22f), new Vector2(RoomX1 - ht - ChicaneGap - ht, 22f));
            WallPiece(t.Root, mark, new Vector2(RoomX0 + ht + ChicaneGap + ht, 25f), new Vector2(RoomX1, 25f));
            // Ledges across the corridors (5.24.0): kerb and threshold sizes, then a proper step.
            foreach (var l in Ledges)
                Box(t.Root, mark, new Vector3(l.x, l.w * 0.5f, l.y), new Vector3(Widths[l.y < 6f ? 0 : 4] + 0.05f, l.w, LedgeDepth), Quaternion.identity);
            // The goal: a flat marker on the floor.
            var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(g.GetComponent<Collider>());
            g.name = "FruitLib_NavCourseGoal";
            g.transform.SetParent(t.Root.transform, false);
            g.transform.localPosition = new Vector3(GoalAt.x, 0.01f, GoalAt.y);
            g.transform.localScale = new Vector3(1.2f, 0.01f, 1.2f);
            var gr = g.GetComponent<Renderer>(); if (gr != null) gr.material = mark;

            _track = t;
            FruitNav.MarkDirty(0f);
            t.BuiltVersion = FruitNav.Version;
            Write($"\n===== walking course built, {_scene}, {DateTime.Now:HH:mm:ss}, FruitLib {FruitVersion.Current} =====\n" +
                  $"  at ({t.O.x:0.0}, {t.O.y:0.0}, {t.O.z:0.0}) facing {Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg:0} deg; corridors {Widths[0]} / {Widths[1]} (door {DoorGap}) / {Widths[2]} / {Widths[3]} / {Widths[4]} m, " +
                  $"room {RoomX1 - RoomX0} x {RoomZ1 - RoomZ0} m with a chicane (gaps {ChicaneGap} m); path margin {FruitNav.PathMargin:0.00} m\n" +
                  $"  ledges {LedgeDepth} m deep across the corridors: 2 cm at 1.6 m, 4 cm at 3.2 m, 7 cm at 4.8 m along the first; 12 cm 1.6 m into the last\n");
            Note($"{Tag} course built; Ctrl+{FruitLibConfig.NavProbeKey} runs every body through it");
        }

        /// <summary>The offset line of leg i on one side: its start (or end) point.</summary>
        private static Vector2 Offset(int leg, int side, bool start)
        {
            Vector2 a = Centre[leg], b = Centre[leg + 1];
            Vector2 d = (b - a).normalized;
            Vector2 nrm = new Vector2(d.y, -d.x) * side * (Widths[leg] * 0.5f + WallT * 0.5f);
            return (start ? a : b) + nrm;
        }

        /// <summary>Where the offset lines of two legs meet on one side.</summary>
        private static Vector2 Join(int l0, int l1, int side)
        {
            Vector2 p1 = Offset(l0, side, true), q1 = Offset(l0, side, false);
            Vector2 p2 = Offset(l1, side, true), q2 = Offset(l1, side, false);
            Vector2 d1 = q1 - p1, d2 = q2 - p2;
            float cr = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(cr) < 1e-4f) return q1;
            Vector2 w = p2 - p1;
            float s = (w.x * d2.y - w.y * d2.x) / cr;
            return p1 + d1 * s;
        }

        private static void WallPiece(GameObject root, Material mat, Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.05f) return;
            Vector2 mid = (a + b) * 0.5f;
            var rot = Quaternion.LookRotation(new Vector3(d.x, 0f, d.y), Vector3.up);
            Box(root, mat, new Vector3(mid.x, WallH * 0.5f, mid.y), new Vector3(WallT, WallH, len + WallT), rot);
        }

        /// <summary>Null if the ground under the course (and the gathering ground behind it) is level
        /// and clear, else what is in the way.</summary>
        private static string Footprint(Track t)
        {
            for (float z = -6f; z <= RoomZ1 + 0.5f; z += 1f)
                for (float x = -2.5f; x <= RoomX1 + 0.5f; x += 1f)
                {
                    // Only the strip the course takes: the corridor's bounding boxes, the room, the gathering ground.
                    if (!InPlan(x, z)) continue;
                    Vector3 p = W(t, x, z);
                    p.y = t.GroundY + 3f;
                    if (!Physics.Raycast(p, Vector3.down, out RaycastHit h, 6f, NavLayers, QueryTriggerInteraction.Ignore))
                        return $"no ground {x:0} m right, {z:0} m ahead";
                    float dy = h.point.y - t.GroundY;
                    if (dy > 0.25f) return $"\"{(h.collider != null ? h.collider.name : "?")}\" {x:0} m right, {z:0} m ahead";
                    if (dy < -0.4f) return $"a drop {x:0} m right, {z:0} m ahead";
                }
            return null;
        }

        private static bool InPlan(float x, float z)
        {
            if (z < 0f) return z >= -6f && x >= -2.5f && x <= 2.5f;
            if (x >= RoomX0 - 0.5f && x <= RoomX1 + 0.5f && z >= RoomZ0 - 0.5f && z <= RoomZ1 + 0.5f) return true;
            for (int i = 0; i + 1 < Centre.Length; i++)
            {
                Vector2 a = Centre[i], b = Centre[i + 1];
                float pad = Widths[i] * 0.5f + 0.6f;
                if (x >= Mathf.Min(a.x, b.x) - pad && x <= Mathf.Max(a.x, b.x) + pad && z >= Mathf.Min(a.y, b.y) - pad && z <= Mathf.Max(a.y, b.y) + pad) return true;
            }
            return false;
        }

        private static void RemoveTrack()
        {
            if (_track == null) return;
            foreach (var e in _track.Order) if (e.Phase != Leg.Done) { try { FruitBodyControl.Stop(e.Body); } catch { } }
            if (_track.Root != null) Object.Destroy(_track.Root);
            _track = null;
            if (FruitNav.Active) FruitNav.MarkDirty(0.5f);
        }

        private static void StartRun()
        {
            var t = _track;
            t.Running.Clear();
            t.Order.Clear();
            t.Runs++;
            foreach (var b in FruitBodies.All)
            {
                if (!b.Alive || !b.Conscious || !b.HasPosition) continue;
                var e = new Entry { Body = b, Phase = Leg.Building };
                t.Running[b.Id] = e;
                t.Order.Add(e);
            }
            if (t.Order.Count == 0) { Note($"{Tag} no bodies to send"); return; }
            // Nearest the start goes first.
            Vector3 start = W(t, 0f, 0f);
            t.Order.Sort((a, b) => FruitNav.Flat(a.Body.Position - start).CompareTo(FruitNav.Flat(b.Body.Position - start)));
            for (int i = 0; i < t.Order.Count; i++) t.Order[i].OldFeet = ((i + t.Runs) & 1) == 0;
            t.Active = true;
            t.Phase = Leg.Building;
            t.PhaseAt = Time.time;
            if (FruitNav.Version == t.BuiltVersion) FruitNav.MarkDirty(0f);
            Write($"\n----- course run {t.Runs}, {t.Order.Count} bodies, {DateTime.Now:HH:mm:ss}: gathering at the start, then one every {Stagger:0} s -----\n");
            Note($"{Tag} course run {t.Runs}: gathering {t.Order.Count} bodies");
        }

        private static void TickTrack()
        {
            var t = _track;
            if (t == null || !t.Active) return;
            float now = Time.time;

            switch (t.Phase)
            {
                case Leg.Building:
                    // The walls have to be in the navmesh before anyone sets off.
                    if (FruitNav.Version == t.BuiltVersion && now - t.PhaseAt < 6f) return;
                    t.BuiltVersion = FruitNav.Version;
                    for (int i = 0; i < t.Order.Count; i++)
                    {
                        var e = t.Order[i];
                        Vector2 spot = Stage(i);
                        // Order first, phase after: the order it replaces (another mod's) ends inside GoTo.
                        e.Phase = Leg.Waiting;
                        FruitBodyControl.GoTo(e.Body, W(t, spot.x, spot.y), 0.6f, GatherSeconds);
                        if (FruitBodyControl.IsWalking(e.Body)) e.Phase = Leg.Gathering;
                    }
                    t.Phase = Leg.Gathering;
                    t.PhaseAt = now;
                    return;

                case Leg.Gathering:
                    bool all = true;
                    foreach (var e in t.Order) if (e.Phase == Leg.Gathering) all = false;
                    if (!all && now - t.PhaseAt < GatherSeconds + 2f) return;
                    Vector3 goal = W(t, GoalAt.x, GoalAt.y);
                    float at = now + 1.5f;
                    foreach (var e in t.Order)
                    {
                        if (e.Phase == Leg.Gathering) { try { FruitBodyControl.Stop(e.Body); } catch { } }
                        e.Phase = Leg.Waiting;
                        e.LaunchAt = at;
                        at += Stagger;
                    }
                    var sb = new StringBuilder("  launch order:");
                    foreach (var e in t.Order) sb.Append($" #{e.Body.Id} {e.Body.Name} ({Way(false, e.OldFeet)})");
                    Write(sb.Append('\n').ToString());
                    t.Phase = Leg.Course;
                    t.PhaseAt = now;
                    return;

                case Leg.Course:
                    bool done = true;
                    Vector3 g = W(t, GoalAt.x, GoalAt.y);
                    foreach (var e in t.Order)
                    {
                        if (e.Phase == Leg.Waiting)
                        {
                            done = false;
                            if (now < e.LaunchAt) continue;
                            if (!e.Body.Alive || !e.Body.Conscious) { e.Phase = Leg.Done; continue; }
                            _summaries.Remove(e.Body.Id);
                            // (GoTo can be false while the order waits for the body's walk; it still runs.)
                            FruitBodyControl.GoTo(e.Body, g, 0.8f, CourseSeconds, false, false, e.OldFeet);
                            e.Phase = FruitBodyControl.IsWalking(e.Body) ? Leg.Course : Leg.Done;
                        }
                        else if (e.Phase == Leg.Course)
                        {
                            // Its order vanished without ending (a map reset clears them): count it out.
                            if (!FruitBodyControl.IsWalking(e.Body)) { e.Phase = Leg.Done; e.Interrupted = true; continue; }
                            done = false;
                        }
                    }
                    if (done) FinishRun();
                    return;
            }
        }

        /// <summary>Gathering spot i: rows of five behind the start.</summary>
        private static Vector2 Stage(int i) => new Vector2(-2.4f + (i % 5) * 1.2f, -2.5f - (i / 5) * 1.5f);

        private static void TrackMoveEnded(FruitBody b, FruitMoveResult r)
        {
            var t = _track;
            if (t == null || !t.Active || !t.Running.TryGetValue(b.Id, out var e)) return;
            if (e.Phase == Leg.Gathering) { e.Phase = Leg.Waiting; return; }
            if (e.Phase != Leg.Course) return;
            e.Phase = Leg.Done;
            _summaries.TryGetValue(b.Id, out e.Summary);
            // Stopped by something other than the course: another mod (NeuroFrukt) gave it an order.
            if (r == FruitMoveResult.Stopped) e.Interrupted = true;
            Write($"  #{b.Id} {b.Name} ({Way(false, e.OldFeet)}, {e.Lifts} step lifts): {(e.Summary != null ? Line(e.Summary) : r.ToString().ToUpperInvariant())}" +
                  $"{(e.Interrupted ? "  [interrupted: another order replaced it]" : "")}\n");
        }

        private static void FinishRun()
        {
            var t = _track;
            t.Active = false;
            t.Phase = Leg.Done;
            var sb = new StringBuilder($"\n  ===== course run {t.Runs} results =====\n");
            sb.Append("  body             way       result    time   path  walked   m/s   off rms/max   sway rms/max  wall  touch  falls  backoff  pivots  shape ms  lifts\n");
            foreach (var e in t.Order)
            {
                var s = e.Summary;
                string name = $"#{e.Body.Id} {e.Body.Name}";
                if (name.Length > 16) name = name.Substring(0, 16);
                if (s == null) { sb.Append($"  {name,-16} {Way(false, e.OldFeet),-8}  did not run\n"); continue; }
                sb.Append($"  {name,-16} {Way(false, e.OldFeet),-8}  {(e.Interrupted ? "INTERRUPT" : s.Result.ToString().ToUpperInvariant()),-8} {s.Seconds,5:0.0}  {s.PathLength,5:0.0}  {s.Walked,5:0.0}  {s.MeanSpeed,5:0.00}   " +
                          $"{s.OffRms,4:0.00} {s.OffMax,4:0.00}     {s.SwayRms,4:0.00} {s.SwayMax,4:0.00}   {s.WallMin,4:0.00}  {s.Contacts,4}  {s.Falls,5}  {s.Retries,7}  {s.Pivots,6}  {s.ShapeMs,7:0.0}  {e.Lifts,5}\n");
            }
            foreach (bool legacy in new[] { true, false })
            {
                int n = 0, arrived = 0, contacts = 0, falls = 0, retries = 0, pivots = 0;
                float time = 0f, offRms = 0f, offMax = 0f, sway = 0f, wall = float.MaxValue, speed = 0f;
                foreach (var e in t.Order)
                {
                    var s = e.Summary;
                    if (e.OldFeet != legacy || s == null || e.Interrupted) continue;
                    n++;
                    if (s.Result == FruitMoveResult.Arrived) { arrived++; time += s.Seconds; }
                    offRms += s.OffRms; offMax = Mathf.Max(offMax, s.OffMax); sway += s.SwayRms; wall = Mathf.Min(wall, s.WallMin);
                    speed += s.MeanSpeed; contacts += s.Contacts; falls += s.Falls; retries += s.Retries; pivots += s.Pivots;
                }
                if (n == 0) continue;
                sb.Append($"  {(legacy ? "old footing (5.23)" : "new footing (5.24)")}: {arrived}/{n} arrived{(arrived > 0 ? $" in {time / arrived:0.0} s on average" : "")}, " +
                          $"off rms {offRms / n:0.00} (worst {offMax:0.00}), sway rms {sway / n:0.00}, nearest wall {wall:0.00} m, {speed / n:0.00} m/s, " +
                          $"{contacts} wall contacts, {falls} falls, {retries} back-offs, {pivots} pivots\n");
            }
            Write(sb.ToString());
            Note($"{Tag} course run {t.Runs} done; results in FruitNav.log. Ctrl+{FruitLibConfig.NavProbeKey} runs it again with the ways swapped");
        }

        // ── S3: stairs ──

        private struct Variant { public string Name; public bool Stairs; public float A, B; }

        private static readonly Variant[] Variants =
        {
            new Variant { Name = "stairs 17 cm rise / 28 cm run", Stairs = true, A = 0.17f, B = 0.28f },
            new Variant { Name = "stairs 20 cm rise / 25 cm run", Stairs = true, A = 0.20f, B = 0.25f },
            new Variant { Name = "ramp 15 deg", A = 15f },
            new Variant { Name = "ramp 25 deg", A = 25f },
            new Variant { Name = "ramp 35 deg", A = 35f },
        };

        private const float LaneWidth = 1.6f, ClimbHeight = 1.5f, LandingLength = 3f;

        private sealed class Climb
        {
            public GameObject Root;
            public FruitBody Body;
            public Variant V;
            public Vector3 Foot, Dir, Right;
            public float Rise, Length;
            public float Started, StartY, MaxGain, NextLog, ProgAlong, ProgAt;
            public int Falls, Steps;
            public bool WasUp = true, Walking;
            public StringBuilder Trace;
        }

        private static Climb _climb;
        private static int _variant;

        private static void RemoveClimb()
        {
            if (_climb == null) return;
            if (_climb.Walking) { try { FruitBodyControl.Stop(_climb.Body); } catch { } }
            if (_climb.Root != null) Object.Destroy(_climb.Root);
            _climb = null;
        }

        private static FruitBody LookedAt()
        {
            if (!FruitBodies.HasView) return null;
            FruitBody best = null;
            float bestA = 12f;
            foreach (var b in FruitBodies.All)
            {
                if (!b.Alive || !b.HasPosition) continue;
                float a = Vector3.Angle(FruitBodies.ViewForward, b.HeadPosition - FruitBodies.ViewPosition);
                if (a < bestA) { bestA = a; best = b; }
            }
            return best;
        }

        private static void StartClimb()
        {
            var body = LookedAt();
            if (body == null) { Note($"{Tag} look at a body to give it the climb"); return; }
            RemoveClimb();
            var v = Variants[_variant % Variants.Length];
            _variant++;

            Vector3 pelvis = body.Limbs[FruitBodies.Pelvis].Present ? body.Limbs[FruitBodies.Pelvis].LivePosition : body.Position;
            Vector3 dir = pelvis - FruitBodies.ViewPosition; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) { dir = body.Facing; dir.y = 0f; }
            dir.Normalize();
            float baseY = Physics.Raycast(pelvis + Vector3.up * 0.2f, Vector3.down, out RaycastHit g, 3f, NavLayers, QueryTriggerInteraction.Ignore)
                ? g.point.y : pelvis.y - 0.95f;
            Vector3 foot = new Vector3(pelvis.x, baseY, pelvis.z) + dir * 1.0f;

            var c = new Climb { Body = body, V = v, Foot = foot, Dir = dir, Right = Vector3.Cross(Vector3.up, dir), Trace = new StringBuilder() };
            c.Root = new GameObject("FruitLib_NavClimb");
            c.Root.transform.position = foot;
            c.Root.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color")) { color = new Color(0.55f, 0.55f, 0.6f) };

            if (v.Stairs)
            {
                int n = Mathf.Max(1, Mathf.RoundToInt(ClimbHeight / v.A));
                c.Rise = n * v.A;
                c.Length = n * v.B;
                for (int i = 0; i < n; i++)
                {
                    float h = (i + 1) * v.A;
                    Box(c.Root, mat, new Vector3(0f, h * 0.5f, i * v.B + v.B * 0.5f), new Vector3(LaneWidth, h, v.B), Quaternion.identity);
                }
            }
            else
            {
                float a = v.A * Mathf.Deg2Rad;
                c.Rise = ClimbHeight;
                c.Length = ClimbHeight / Mathf.Tan(a);
                float slab = 0.3f, slope = ClimbHeight / Mathf.Sin(a);
                var rot = Quaternion.Euler(-v.A, 0f, 0f);
                Vector3 topMid = new Vector3(0f, ClimbHeight * 0.5f, c.Length * 0.5f);
                Vector3 normal = rot * Vector3.up;
                Box(c.Root, mat, topMid - normal * (slab * 0.5f), new Vector3(LaneWidth, slab, slope), rot);
            }
            Box(c.Root, mat, new Vector3(0f, c.Rise * 0.5f, c.Length + LandingLength * 0.5f), new Vector3(LaneWidth, c.Rise, LandingLength), Quaternion.identity);

            c.Started = Time.time;
            c.StartY = pelvis.y;
            c.NextLog = 0f;
            c.ProgAlong = Along(c, pelvis);
            c.ProgAt = Time.time;
            c.Walking = true;
            _climb = c;
            c.Trace.Append($"\n===== S3 climb (step assist {(FruitLibConfig.StepAssist ? "on" : "off")}): {v.Name}, {c.Rise:0.00} m over {c.Length:0.00} m, body #{body.Id} {body.Name}, {_scene}, {DateTime.Now:HH:mm:ss} =====\n");
            c.Trace.Append("    t     along  gain   behaviour    posture   balance\n");
            Note($"{Tag} climb test: {v.Name} for #{body.Id} {body.Name}");
        }

        private static void Box(GameObject parent, Material mat, Vector3 localPos, Vector3 size, Quaternion localRot)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "FruitLib_NavTestPart";
            go.layer = EnvLayer;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            if (r != null) r.material = mat;
        }

        private static float Along(Climb c, Vector3 p) { Vector3 d = p - c.Foot; d.y = 0f; return Vector3.Dot(d, c.Dir); }

        private static void TickClimb()
        {
            var c = _climb;
            if (c == null || !c.Walking) return;
            var b = c.Body;
            float t = Time.time - c.Started;
            if (b == null || !b.Alive || FruitBodies.Get(b.Id) != b) { FinishClimb(c, $"body gone or dead after {t:0.0} s"); return; }

            Vector3 p = b.Limbs[FruitBodies.Pelvis].Present ? b.Limbs[FruitBodies.Pelvis].LivePosition : b.Position;
            float along = Along(c, p), gain = p.y - c.StartY;
            c.MaxGain = Mathf.Max(c.MaxGain, gain);
            bool up = b.Behaviour == FruitBehaviour.Standing || b.Behaviour == FruitBehaviour.Moving;
            if (c.WasUp && !up) c.Falls++;
            c.WasUp = up;

            if (t >= c.NextLog)
            {
                c.NextLog = t + 0.25f;
                c.Trace.Append($"  {t,5:0.00}  {along,5:0.00}  {gain,5:0.00}   {b.Behaviour,-11}  {b.Posture,-8}  {b.Balance,4:0}\n");
            }

            if (along > c.Length + 1.2f && gain > c.Rise - 0.35f)
            {
                FinishClimb(c, $"REACHED the top in {t:0.0} s, {c.Falls} falls");
                return;
            }
            if (along > c.ProgAlong + 0.2f) { c.ProgAlong = along; c.ProgAt = Time.time; }
            else if (Time.time - c.ProgAt > 4f && up)
            {
                FinishClimb(c, $"STUCK at {along:0.00} m along ({(c.V.Stairs ? $"step {Mathf.FloorToInt(along / c.V.B) + 1}" : "on the ramp")}), " +
                                $"{gain:0.00} m up (best {c.MaxGain:0.00}), {c.Falls} falls, after {t:0.0} s");
                return;
            }
            if (t > 30f) { FinishClimb(c, $"TIMED OUT at {along:0.00} m along, {gain:0.00} m up, {c.Falls} falls"); return; }

            // Hold the lane: steer back toward its centre line.
            Vector3 d = p - c.Foot; d.y = 0f;
            float side = Vector3.Dot(d, c.Right);
            Vector3 heading = c.Dir - c.Right * Mathf.Clamp(side * 0.8f, -0.6f, 0.6f);
            FruitBodyControl.Walk(b, heading, 1f);
        }

        private static void FinishClimb(Climb c, string outcome)
        {
            c.Walking = false;
            try { FruitBodyControl.Stop(c.Body); } catch { }
            c.Trace.Append($"  outcome: {outcome}; step assist lifted it {c.Steps} times\n");
            Write(c.Trace.ToString());
            Note($"{Tag} {c.V.Name}: {outcome}");
        }

        // ── Log ──

        private static void Write(string s)
        {
            try
            {
                if (_log == null)
                {
                    _log = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
                    _log.WriteLine($"# FruitNav probe, FruitLib {FruitVersion.Current}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
                _log.Write(s);
            }
            catch { }
        }

        private static void Note(string s) => MelonLogger.Msg(s);
    }
}
