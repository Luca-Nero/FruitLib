using System;
using System.Collections.Generic;
using System.Diagnostics;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;

namespace FruitLib
{
    /// <summary>How much of the way a <see cref="FruitPath"/> gets. Since 5.19.0.</summary>
    public enum FruitPathStatus
    {
        /// <summary>No path: one end is off the navmesh, or there is no navmesh yet.</summary>
        None,
        /// <summary>Gets as close as the navmesh allows, but not there: the goal is cut off.</summary>
        Partial,
        /// <summary>All the way.</summary>
        Complete,
    }

    /// <summary>A walkable route as corners on the navmesh, with how much room there is along it. Since 5.19.0.</summary>
    public sealed class FruitPath
    {
        public FruitPathStatus Status;
        /// <summary>Start, turns, end; on the navmesh surface.</summary>
        public readonly List<Vector3> Corners = new List<Vector3>(16);
        /// <summary>Total length, metres.</summary>
        public float Length;
        /// <summary>Room either side of the line (distance to the nearest navmesh edge, so on top
        /// of the agent radius already kept from walls), sampled every <see cref="FruitNav.RoomStep"/>
        /// metres from the start.</summary>
        public readonly List<float> Room = new List<float>(64);
        /// <summary>The navmesh build it was found on (<see cref="FruitNav.Version"/>).</summary>
        public int Version;
        /// <summary>Whether it was shaped to keep its distance from walls (<see cref="FruitNav.Shape"/>). Since 5.21.0.</summary>
        public bool Shaped;
        /// <summary>Flat distance from the start to each corner: <c>Along[i]</c> is how far corner i is. Since 5.21.0.</summary>
        public readonly List<float> Along = new List<float>(16);

        public bool Found => Status != FruitPathStatus.None && Corners.Count >= 2;
        public Vector3 End => Corners.Count > 0 ? Corners[Corners.Count - 1] : Vector3.zero;

        /// <summary>The point <paramref name="distance"/> metres (flat) from the start. Since 5.21.0.</summary>
        public Vector3 PointAt(float distance)
        {
            int n = Corners.Count;
            if (n == 0) return Vector3.zero;
            if (distance <= 0f || n == 1) return Corners[0];
            if (distance >= Length) return Corners[n - 1];
            // Binary search on Along: paths that were shaped have a corner every 30 cm.
            int lo = 1, hi = n - 1;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (Along[mid] < distance) lo = mid + 1; else hi = mid; }
            float seg = Along[lo] - Along[lo - 1];
            return Vector3.Lerp(Corners[lo - 1], Corners[lo], seg > 1e-4f ? (distance - Along[lo - 1]) / seg : 1f);
        }

        /// <summary>The flat direction of travel <paramref name="distance"/> metres from the start. Since 5.21.0.</summary>
        public Vector3 DirectionAt(float distance)
        {
            int n = Corners.Count;
            if (n < 2) return Vector3.forward;
            int i = 1;
            while (i < n - 1 && Along[i] < distance) i++;
            Vector3 d = Corners[i] - Corners[i - 1]; d.y = 0f;
            float m = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            return m > 1e-4f ? d / m : Vector3.forward;
        }

        /// <summary>Recomputes <see cref="Along"/> and <see cref="Length"/> from the corners.</summary>
        internal void Measure()
        {
            Along.Clear();
            Length = 0f;
            for (int i = 0; i < Corners.Count; i++)
            {
                if (i > 0) Length += FruitNav.Flat(Corners[i] - Corners[i - 1]);
                Along.Add(Length);
            }
        }

        /// <summary>The least room within <paramref name="ahead"/> metres after <paramref name="along"/>.</summary>
        public float RoomAhead(float along, float ahead)
        {
            if (Room.Count == 0) return float.PositiveInfinity;
            int a = Mathf.Clamp(Mathf.FloorToInt(along / FruitNav.RoomStep), 0, Room.Count - 1);
            int b = Mathf.Clamp(Mathf.CeilToInt((along + ahead) / FruitNav.RoomStep), 0, Room.Count - 1);
            float m = float.PositiveInfinity;
            for (int i = a; i <= b; i++) if (Room[i] < m) m = Room[i];
            return m;
        }

        internal void Clear() { Status = FruitPathStatus.None; Corners.Clear(); Room.Clear(); Along.Clear(); Length = 0f; Version = 0; Shaped = false; }
    }

    /// <summary>
    /// Pathfinding for bodies. FRUKT ships no navmesh and its build has none of Unity's managed
    /// navmesh building, but the engine side survives, so FruitNav builds one at runtime through
    /// the icalls (<see cref="FruitNavNative"/>) in a box around the player and every tracked
    /// body, from the colliders on the map and spawned props. It is rebuilt in the background
    /// (12-30 ms off the main thread for an 80 m box, under 1 ms on it) when bodies leave the box,
    /// a few seconds after an explosion, and every few seconds while anyone walks a path, so
    /// moved props and blasted walls are picked up.
    ///
    /// <code>
    /// FruitNav.Require("MyMod");
    /// var path = new FruitPath();
    /// if (FruitNav.FindPath(body.Position, shopDoor, path) &amp;&amp; path.Status == FruitPathStatus.Complete) ...
    /// </code>
    ///
    /// FruitBodyControl's goto orders (WalkTo, Approach, Follow, Attack, WalkAway) follow paths on
    /// their own once FruitNav is running; nothing else needs to call it.
    ///
    /// The agent is a FRUKT body: 0.38 m radius (gaps under ~0.8 m are closed: bodies sway and
    /// shoulders catch), 1.8 m tall, slopes up to 20 degrees (a 15 degree ramp is walkable, 25 is
    /// not), and steps up to 0.3 m with the step assist on (bodies can't lift their feet onto
    /// anything without it, so steps stay walls when it's off).
    /// </summary>
    /// <remarks>Since 5.19.0.</remarks>
    public static class FruitNav
    {
        public const float AgentRadius = 0.38f, AgentHeight = 1.8f, AgentSlope = 20f;
        public const float StepWithAssist = 0.3f, StepWithout = 0.06f;
        public const float RoomStep = 0.5f;

        /// <summary>Default, Environment, Spawnable: the map and the props spawned onto it.</summary>
        public const int Layers = (1 << 0) | (1 << 8) | (1 << 12);

        /// <summary>Bumped each time a build lands; paths from older builds should be found again.</summary>
        public static int Version { get; private set; }

        /// <summary>A navmesh is loaded and answering.</summary>
        public static bool Ready => Version > 0 && _data != null && _data.Added;

        /// <summary>FruitNav runs while any mod requires it (or the pathing test is on).</summary>
        public static bool Active => _requiredBy.Count > 0 || FruitLibConfig.NavProbe || FruitLibConfig.NavDraw;

        /// <summary>The box the current navmesh covers.</summary>
        public static Bounds Region => _region;

        /// <summary>Raised on the main thread when a build lands.</summary>
        public static event Action Rebuilt;

        private static readonly HashSet<string> _requiredBy = new HashSet<string>();

        public static void Require(string mod)
        {
            if (_requiredBy.Add(mod ?? "?")) FruitLog.Info($"[FruitNav] building navmeshes for {mod}.");
            FruitBodies.Require("FruitNav");
        }

        /// <summary>Asks for a rebuild soon, e.g. after a mod moved or removed something large.</summary>
        public static void MarkDirty(float delay = 1f)
        {
            float at = Time.time + Mathf.Max(0f, delay);
            if (_dirtyAt < 0f || at < _dirtyAt) _dirtyAt = at;
        }

        // ── Queries ──

        /// <summary>The nearest navmesh point within <paramref name="maxDistance"/>.</summary>
        public static bool Sample(Vector3 point, float maxDistance, out Vector3 onMesh)
        {
            onMesh = point;
            if (!Ready) return false;
            if (!NavMesh.SamplePosition(point, out NavMeshHit h, maxDistance, NavMesh.AllAreas)) return false;
            onMesh = h.position;
            return true;
        }

        private static NavMeshPath _query;

        /// <summary>
        /// A path between two points (each snapped to the navmesh within a few metres; a pelvis or a
        /// point on a wall will do). False if there is no navmesh or no route at all; a partial
        /// route comes back as <see cref="FruitPathStatus.Partial"/>.
        /// </summary>
        public static bool FindPath(Vector3 from, Vector3 to, FruitPath path, float snap = 3f)
            => FindPath(from, to, path, snap, true);

        /// <summary>As above; <paramref name="measureRoom"/> false skips sampling the room along the
        /// path (<see cref="FruitPath.Room"/> stays empty), for "can I get there, how far" questions. Since 5.20.0.</summary>
        public static bool FindPath(Vector3 from, Vector3 to, FruitPath path, float snap, bool measureRoom)
            => FindPath(from, to, path, snap, measureRoom, false);

        /// <summary>As above; <paramref name="shape"/> also runs <see cref="Shape"/> on it, so it keeps
        /// <see cref="PathMargin"/> from walls where there is room. FruitBodyControl's goto orders
        /// walk shaped paths. Since 5.21.0.</summary>
        public static bool FindPath(Vector3 from, Vector3 to, FruitPath path, float snap, bool measureRoom, bool shape)
        {
            path.Clear();
            if (!Ready) return false;
            try
            {
                if (!Snap(from, 2.5f, out Vector3 a) || !Snap(to, snap, out Vector3 b)) return false;
                _query ??= new NavMeshPath();
                if (!NavMesh.CalculatePath(a, b, NavMesh.AllAreas, _query)) return false;
                var st = _query.status;
                if (st == NavMeshPathStatus.PathInvalid) return false;
                Vector3[] corners = _query.corners;
                if (corners == null || corners.Length < 2) return false;
                for (int i = 0; i < corners.Length; i++) path.Corners.Add(corners[i]);
                path.Measure();
                path.Status = st == NavMeshPathStatus.PathComplete ? FruitPathStatus.Complete : FruitPathStatus.Partial;
                path.Version = Version;
                if (shape) Shape(path);
                if (measureRoom) MeasureRoom(path);
                return true;
            }
            catch (Exception e)
            {
                ReportOnce("path query", e);
                path.Clear();
                return false;
            }
        }

        /// <summary>Snaps to the navmesh, preferring the floor under the point over a surface beside it.</summary>
        private static bool Snap(Vector3 p, float maxDistance, out Vector3 onMesh)
        {
            // A pelvis is ~1 m over its floor; sampling from there can find the storey above's slab first.
            if (NavMesh.SamplePosition(p + Vector3.down * 0.9f, out NavMeshHit h, Mathf.Min(1.2f, maxDistance), NavMesh.AllAreas))
            { onMesh = h.position; return true; }
            if (NavMesh.SamplePosition(p, out h, maxDistance, NavMesh.AllAreas)) { onMesh = h.position; return true; }
            onMesh = p;
            return false;
        }

        /// <summary>The navmesh point under a pelvis (or a chest): the floor beneath it, within
        /// <paramref name="maxDistance"/>. How far it is off the pelvis, flat, says whether the
        /// body is out in the margin the agent radius keeps from walls. Since 5.33.0.</summary>
        internal static bool Under(Vector3 pelvis, float maxDistance, out Vector3 onMesh)
        {
            onMesh = pelvis;
            if (!Ready) return false;
            try { return Snap(pelvis, maxDistance, out onMesh); }
            catch { return false; }
        }

        /// <summary>True if a body could walk straight from one point to the other on the navmesh.</summary>
        public static bool StraightClear(Vector3 from, Vector3 to)
        {
            if (!Ready) return false;
            try
            {
                if (!Snap(from, 2.5f, out Vector3 a) || !Snap(to, 2.5f, out Vector3 b)) return false;
                return !FruitNavNative.Raycast(a, b, out _);
            }
            catch (Exception e) { ReportOnce("straight line", e); return false; }
        }

        /// <summary>Distance from a navmesh point to the nearest edge of the walkable area (on top of
        /// the agent radius kept from walls); infinity off the navmesh.</summary>
        public static float Room(Vector3 onMesh)
        {
            if (!Ready) return float.PositiveInfinity;
            try { return FruitNavNative.ClosestEdge(onMesh, out var h) ? h.Distance : float.PositiveInfinity; }
            catch { return float.PositiveInfinity; }
        }

        /// <summary>
        /// A reachable point roughly <paramref name="distance"/> metres from <paramref name="from"/>
        /// and away from <paramref name="threat"/>, with the path to it: straight away first, then
        /// fanning out to the sides. The farthest-from-the-threat of the complete ones wins.
        /// </summary>
        public static bool FindAway(Vector3 from, Vector3 threat, float distance, FruitPath path)
        {
            path.Clear();
            if (!Ready) return false;
            Vector3 away = from - threat; away.y = 0f;
            if (away.sqrMagnitude < 1e-4f) away = Vector3.forward;
            away.Normalize();
            float startD = Flat(from - threat);
            float best = float.NegativeInfinity;
            var trial = new FruitPath();
            foreach (float yaw in AwayFan)
            {
                Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * away;
                Vector3 goal = from + dir * distance;
                if (!FindPath(from, goal, trial, 4f)) continue;
                if (trial.Status != FruitPathStatus.Complete) continue;
                float gain = Flat(trial.End - threat) - startD;
                if (gain < distance * 0.4f) continue;
                // Prefer getting far, but not by a long detour.
                float score = gain - 0.3f * Mathf.Max(0f, trial.Length - distance);
                if (score > best) { best = score; Copy(trial, path); }
            }
            return path.Found;
        }

        private static readonly float[] AwayFan = { 0f, 30f, -30f, 60f, -60f, 95f, -95f };

        private static readonly float[] CoverRings = { 2f, 3.5f, 5f, 7f, 9.5f, 12.5f, 16f, 20f };
        private struct Spot { public Vector3 P; public float Score; }
        private static readonly List<Spot> _spots = new List<Spot>(128);

        /// <summary>
        /// The nearest reachable spot within <paramref name="maxDistance"/> where something solid
        /// stands between a body (chest and head height) and <paramref name="threat"/> (an
        /// explosion, a gunman's eyes, the player's camera), with the path to it. Spots toward the
        /// threat count as further away. False when nothing near gives cover. Since 5.20.0; spots
        /// someone stands on or is headed for are skipped since 5.22.0.
        /// </summary>
        public static bool FindCover(Vector3 from, Vector3 threat, float maxDistance, FruitPath path)
        {
            path.Clear();
            if (!Ready) return false;
            try
            {
                if (!Snap(from, 2.5f, out Vector3 start)) return false;
                Vector3 eye = threat;
                // A point on the ground (a blast) sees over low cover from a little above it.
                if (Physics.Raycast(threat + Vector3.up * 0.3f, Vector3.down, 0.6f, Layers, QueryTriggerInteraction.Ignore)) eye.y += 0.5f;
                float fromThreat = Flat(start - threat);
                if (Hidden(eye, start)) { path.Status = FruitPathStatus.Complete; path.Corners.Add(start); path.Corners.Add(start); path.Version = Version; return true; }

                _spots.Clear();
                foreach (float r in CoverRings)
                {
                    if (r > maxDistance) break;
                    for (int k = 0; k < 16; k++)
                    {
                        float a = k * (Mathf.PI * 2f / 16f) + r * 0.37f;   // rings staggered
                        Vector3 p = start;
                        p.x += Mathf.Cos(a) * r; p.z += Mathf.Sin(a) * r;
                        if (!NavMesh.SamplePosition(p, out NavMeshHit h, 1.2f, NavMesh.AllAreas)) continue;
                        if (!Hidden(eye, h.position)) continue;
                        // Someone hides there already (or is on the way): bodies hide apart. Since 5.22.0.
                        if (FruitBodyControl.SpreadOut && FruitBodyControl.Taken(h.position)) continue;
                        float closer = Mathf.Max(0f, fromThreat - Flat(h.position - threat));
                        _spots.Add(new Spot { P = h.position, Score = r + closer * 2f });
                    }
                }
                if (_spots.Count == 0) return false;
                _spots.Sort((x, y) => x.Score.CompareTo(y.Score));

                var trial = new FruitPath();
                int tries = 0;
                foreach (var sp in _spots)
                {
                    if (++tries > 8) break;
                    if (!FindPath(start, sp.P, trial, 1f, true) || trial.Status != FruitPathStatus.Complete) continue;
                    if (trial.Length > maxDistance * 1.8f) continue;
                    Copy(trial, path);
                    return true;
                }
                return false;
            }
            catch (Exception e) { ReportOnce("cover search", e); path.Clear(); return false; }
        }

        /// <summary>Whether a body standing on <paramref name="spot"/> is out of sight from <paramref name="eye"/>.</summary>
        public static bool Hidden(Vector3 eye, Vector3 spot)
        {
            foreach (float h in CoverHeights)
            {
                Vector3 t = spot; t.y += h;
                Vector3 d = t - eye;
                float len = d.magnitude;
                if (len < 0.3f) return false;
                if (!Physics.Raycast(eye, d / len, len - 0.15f, Layers, QueryTriggerInteraction.Ignore)) return false;
            }
            return true;
        }

        private static readonly float[] CoverHeights = { 1.0f, 1.55f };

        /// <summary>Copies one path into another. Internal since 5.21.0.</summary>
        internal static void Copy(FruitPath from, FruitPath to)
        {
            to.Clear();
            to.Status = from.Status; to.Length = from.Length; to.Version = from.Version; to.Shaped = from.Shaped;
            to.Corners.AddRange(from.Corners);
            to.Along.AddRange(from.Along);
            to.Room.AddRange(from.Room);
        }

        // ── Shaping ──
        //
        // The navmesh's own paths are the shortest there are: pulled tight round every corner, they
        // touch the navmesh edge (the agent radius from the wall) at each one. A FRUKT body can't
        // walk that line. It sways side to side with every step, swings wide on turns and lags
        // behind its heading, so a path that grazes every corner has its shoulders on the wall.
        //
        // Shape turns the corner list into an elastic band: points every ShapeStep metres, each
        // relaxed toward its neighbours (which rounds the corners off) and pushed away from the
        // nearest navmesh edge until it has PathMargin of room (which keeps it off the walls and
        // centres it in anything narrower than twice that). Every move is checked against the
        // navmesh, the ends stay put, and if the result doesn't hold up the plain path is kept.

        /// <summary>Room, in metres beyond the agent radius, a shaped path keeps from the edge of the
        /// walkable area wherever there is that much: 0.38 + 0.3 puts a pelvis 0.68 m from a wall.
        /// From <see cref="FruitLibConfig.NavPathMargin"/>. Since 5.21.0.</summary>
        public static float PathMargin => Mathf.Clamp(FruitLibConfig.NavPathMargin, 0f, 1.5f);

        /// <summary>Spacing of the points of a shaped path, metres.</summary>
        public const float ShapeStep = 0.3f;
        /// <summary>Relaxation passes.</summary>
        public static int ShapePasses = 8;

        /// <summary>Milliseconds the last <see cref="Shape"/> took. For the pathing test.</summary>
        public static double LastShapeMs { get; private set; }

        private static readonly List<Vector3> _band = new List<Vector3>(256), _bandWas = new List<Vector3>(256);

        /// <summary>
        /// Reshapes a found path in place to keep <see cref="PathMargin"/> from walls where there is
        /// room, with its corners rounded. Leaves it as it was (and false) if the shaped line
        /// would leave the navmesh anywhere. Since 5.21.0.
        /// </summary>
        public static bool Shape(FruitPath path)
        {
            if (!Ready || path == null || !path.Found || path.Length < ShapeStep * 2f) return false;
            var sw = Stopwatch.StartNew();
            try
            {
                Resample(path, _band);
                int n = _band.Count;
                if (n < 3) return false;
                float margin = PathMargin;
                _bandWas.Clear();
                _bandWas.AddRange(_band);

                for (int pass = 0; pass < ShapePasses; pass++)
                {
                    for (int i = 1; i < n - 1; i++)
                    {
                        Vector3 p = _band[i], prev = _band[i - 1], next = _band[i + 1];

                        // Tension: halfway to the midpoint of the neighbours, as far as the navmesh allows.
                        Vector3 s = p;
                        s.x += ((prev.x + next.x) * 0.5f - p.x) * 0.5f;
                        s.z += ((prev.z + next.z) * 0.5f - p.z) * 0.5f;
                        s.y += ((prev.y + next.y) * 0.5f - p.y) * 0.5f;
                        s = Reach(p, s);

                        // Room: away from the nearest edge, half the shortfall a pass (from both walls
                        // of a narrow passage in turn, that settles in its middle).
                        if (margin > 0f && FruitNavNative.ClosestEdge(s, out var h) && h.Distance < margin)
                        {
                            float ax = s.x - h.Position.x, az = s.z - h.Position.z;
                            float m = MathF.Sqrt(ax * ax + az * az);
                            bool onEdge = m < 0.01f;
                            if (onEdge) { ax = h.Normal.x; az = h.Normal.z; m = MathF.Sqrt(ax * ax + az * az); }
                            if (m > 1e-4f)
                            {
                                float push = MathF.Min((margin - h.Distance) * 0.5f, 0.15f) / m;
                                Vector3 t = s;
                                t.x += ax * push; t.z += az * push;
                                t = Reach(s, t);
                                // On an edge the normal's sense is not certain: keep the move only if it found room.
                                if (onEdge && (!FruitNavNative.ClosestEdge(t, out var h2) || h2.Distance <= h.Distance))
                                {
                                    t = s; t.x -= ax * push; t.z -= az * push;
                                    t = Reach(s, t);
                                }
                                s = t;
                            }
                        }
                        _band[i] = s;
                    }
                }

                // Every leg has to be walkable; if not, the plain path stands.
                for (int i = 1; i < n; i++)
                    if (FruitNavNative.Raycast(_band[i - 1], _band[i], out _))
                    {
                        Note("a shaped path left the navmesh; walking the plain one");
                        return false;
                    }

                path.Corners.Clear();
                path.Corners.AddRange(_band);
                path.Measure();
                path.Shaped = true;
                return true;
            }
            catch (Exception e) { ReportOnce("path shaping", e); return false; }
            finally { LastShapeMs = sw.Elapsed.TotalMilliseconds; }
        }

        /// <summary>From a navmesh point toward another: the other if the straight line stays on the
        /// navmesh, otherwise most of the way to where it would leave it.</summary>
        private static Vector3 Reach(Vector3 from, Vector3 to)
        {
            if (!FruitNavNative.Raycast(from, to, out var h)) return to;
            Vector3 r = from;
            r.x += (h.Position.x - from.x) * 0.8f;
            r.z += (h.Position.z - from.z) * 0.8f;
            r.y += (h.Position.y - from.y) * 0.8f;
            return r;
        }

        /// <summary>The path's corners as points every <see cref="ShapeStep"/>, ends and corners kept.</summary>
        private static void Resample(FruitPath path, List<Vector3> into)
        {
            into.Clear();
            var c = path.Corners;
            into.Add(c[0]);
            for (int i = 1; i < c.Count; i++)
            {
                Vector3 a = c[i - 1], b = c[i];
                float seg = path.Along[i] - path.Along[i - 1];
                int k = Math.Max(1, (int)MathF.Round(seg / ShapeStep));
                for (int j = 1; j <= k; j++)
                {
                    float t = (float)j / k;
                    Vector3 p = a;
                    p.x += (b.x - a.x) * t; p.y += (b.y - a.y) * t; p.z += (b.z - a.z) * t;
                    if (j < k || i == c.Count - 1 || seg > 0.05f) into.Add(p);
                }
            }
        }

        private static readonly HashSet<string> _noted = new HashSet<string>();
        private static void Note(string what) { if (_noted.Add(what)) FruitLog.Info($"[FruitNav] {what} (said once per scene)."); }

        private static void MeasureRoom(FruitPath path)
        {
            path.Room.Clear();
            float along = 0f;
            for (int i = 1; i < path.Corners.Count; i++)
            {
                Vector3 a = path.Corners[i - 1], b = path.Corners[i];
                float seg = Flat(b - a);
                while (path.Room.Count * RoomStep <= along + seg && path.Room.Count < 1024)
                {
                    float t = seg > 1e-4f ? (path.Room.Count * RoomStep - along) / seg : 0f;
                    Vector3 p = Vector3.Lerp(a, b, Mathf.Clamp01(t));
                    path.Room.Add(FruitNavNative.ClosestEdge(p, out var h) ? h.Distance : float.PositiveInfinity);
                }
                along += seg;
            }
        }

        /// <summary>Length of a vector on the ground plane (y ignored). Public since 5.20.0.</summary>
        public static float Flat(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);

        // ── Building ──

        private static FruitNavNative.Data _data;
        private static readonly FruitNavNative.SourceList _list = new FruitNavNative.SourceList();
        private static FruitNavNative.BuildSettings _settings;
        private static bool _checked, _usable, _settingsMade, _builtWithAssist, _subscribed;
        private static IntPtr _op;
        private static Bounds _region, _building;
        private static float _dirtyAt = -1f, _refreshAt, _opStart, _lastStart = -100f;

        /// <summary>Least seconds between two builds. A box moves on a 16 m grid, so walking about
        /// inside it changes nothing; only leaving it does.</summary>
        public static float MinInterval = 1.5f;
        private const float Grid = 16f;
        private static int _opSources;
        private static double _opMainMs;

        /// <summary>Seconds between refreshes while a body walks a path; 4x that otherwise.</summary>
        public static float RefreshSeconds = 4f;
        /// <summary>The navmesh box is at least this wide, and at most <see cref="FruitLibConfig.NavMaxSize"/>.</summary>
        public static float MinSize = 60f;
        private const float Pad = 15f, Margin = 8f;

        /// <summary>Last build: sources, wall time, main-thread time. For the pathing test.</summary>
        public static string LastBuild { get; private set; } = "";

        internal static void Tick()
        {
            if (!Active) return;
            if (!_subscribed) { FruitEvents.Raised += OnEvent; _subscribed = true; }
            if (!CheckOnce()) return;

            if (_op != IntPtr.Zero) { PollBuild(); return; }

            // Step height follows the step assist; a change of mind means a fresh build.
            if (_settingsMade && _builtWithAssist != FruitLibConfig.StepAssist) { _settingsMade = false; _dirtyAt = 0f; }

            if (!Needed(out Bounds need)) return;
            float now = Time.time;
            bool outside = Version == 0 || !ContainsFlat(_region, need);
            bool dirty = _dirtyAt >= 0f && now >= _dirtyAt;
            bool refresh = now >= _refreshAt;
            if (!outside && !dirty && !refresh) return;
            // A moving camera (or body) would otherwise rebuild every few frames.
            if (Version > 0 && now - _lastStart < MinInterval) return;
            _lastStart = now;
            StartBuild(outside ? Grow(need) : Regrow(need));
        }

        private static bool CheckOnce()
        {
            if (_checked) return _usable;
            _checked = true;
            var missing = new List<string>();
            foreach (var n in FruitNavNative.Names) if (!FruitNavNative.Has(n)) missing.Add(n);
            _usable = missing.Count == 0 && FruitNavNative.LayoutOk;
            if (!_usable)
                MelonLogger.Warning($"[FruitNav] can't build navmeshes in this game build ({(FruitNavNative.LayoutOk ? "missing: " + string.Join(", ", missing) : "struct layout changed")}); bodies walk straight lines.");
            return _usable;
        }

        /// <summary>The box the player and every live body need, padded.</summary>
        private static bool Needed(out Bounds need)
        {
            need = default;
            bool any = false;
            Vector3 min = default, max = default;
            void Take(Vector3 p)
            {
                if (!any) { min = max = p; any = true; return; }
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            if (FruitBodies.HasView) Take(FruitBodies.ViewPosition);
            foreach (var b in FruitBodies.All)
            {
                if (!b.Alive || !b.HasPosition) continue;
                Take(b.Position);
                // Where it is headed too, or a far goal falls outside the box and reads as cut off.
                if (FruitBodyControl.GoalOf(b, out Vector3 g)) Take(g);
            }
            if (!any) return false;
            min -= new Vector3(Pad, 12f, Pad);
            max += new Vector3(Pad, 12f, Pad);
            need = new Bounds((min + max) * 0.5f, max - min);
            return true;
        }

        /// <summary>A new box: what is needed plus a margin, at least MinSize wide, at most NavMaxSize
        /// (then centred on the player, who is where things are watched).</summary>
        private static Bounds Grow(Bounds need)
        {
            Vector3 size = need.size;
            size.x = Mathf.Max(size.x + Margin * 2f, MinSize);
            size.z = Mathf.Max(size.z + Margin * 2f, MinSize);
            Vector3 c = need.center;
            float max = Mathf.Clamp(FruitLibConfig.NavMaxSize, 40f, 600f);
            if (size.x > max || size.z > max)
            {
                if (FruitBodies.HasView) { c.x = FruitBodies.ViewPosition.x; c.z = FruitBodies.ViewPosition.z; }
                size.x = Mathf.Min(size.x, max);
                size.z = Mathf.Min(size.z, max);
            }
            // Snap outward to the grid, so small moves land on the same box.
            float x0 = Mathf.Floor((c.x - size.x * 0.5f) / Grid) * Grid, x1 = Mathf.Ceil((c.x + size.x * 0.5f) / Grid) * Grid;
            float z0 = Mathf.Floor((c.z - size.z * 0.5f) / Grid) * Grid, z1 = Mathf.Ceil((c.z + size.z * 0.5f) / Grid) * Grid;
            float y0 = Mathf.Floor((c.y - size.y * 0.5f) / 4f) * 4f, y1 = Mathf.Ceil((c.y + size.y * 0.5f) / 4f) * 4f;
            return new Bounds(new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, y1 - y0, z1 - z0));
        }

        /// <summary>A refresh keeps the box unless it has grown too big for what is needed now.</summary>
        private static Bounds Regrow(Bounds need)
        {
            var g = Grow(need);
            return _region.size.x > g.size.x * 1.6f || _region.size.z > g.size.z * 1.6f ? g : _region;
        }

        private static bool ContainsFlat(Bounds outer, Bounds inner)
            => inner.min.x >= outer.min.x && inner.max.x <= outer.max.x &&
               inner.min.z >= outer.min.z && inner.max.z <= outer.max.z &&
               inner.min.y >= outer.min.y - 6f && inner.max.y <= outer.max.y + 6f;

        private static void MakeSettings()
        {
            var s = FruitNavNative.SettingsById(0);
            if (s.AgentTypeID != 0 || s.AgentRadius <= 0f)
                s = new FruitNavNative.BuildSettings { AgentTypeID = 0, MinRegionArea = 2f, TileSize = 256 };
            s.AgentRadius = AgentRadius;
            s.AgentHeight = AgentHeight;
            s.AgentSlope = AgentSlope;
            _builtWithAssist = FruitLibConfig.StepAssist;
            s.AgentClimb = _builtWithAssist ? Mathf.Clamp(FruitLibConfig.NavClimb, 0.1f, 0.32f) : StepWithout;
            s.LedgeDropHeight = 0f;
            s.MaxJumpAcrossDistance = 0f;
            s.MinRegionArea = 1f;
            // 10 cm cells: the radius erodes 4 cells off each wall, which still leaves a 1 m door open.
            s.OverrideVoxelSize = 1;
            s.VoxelSize = 0.1f;
            s.OverrideTileSize = 0;
            s.BuildHeightMesh = 1;
            s.PreserveTilesOutsideBounds = 0;
            _settings = s;
            _settingsMade = true;
        }

        private static void StartBuild(Bounds region)
        {
            float now = Time.time;
            _dirtyAt = -1f;
            _refreshAt = now + (FruitBodyControl.PathWalkers > 0 ? RefreshSeconds : RefreshSeconds * 4f);
            try
            {
                if (!_settingsMade) MakeSettings();
                if (_data == null)
                {
                    _data = FruitNavNative.CreateData(_settings.AgentTypeID);
                    FruitNavNative.Add(_data);
                }
                var sw = Stopwatch.StartNew();
                var src = FruitNavNative.CollectSources(region, Layers, true, out _);
                _list.Set(src, src.Length);
                _opSources = src.Length;
                _building = region;
                _op = FruitNavNative.UpdateAsync(_data, ref _settings, _list, region);
                _opMainMs = sw.Elapsed.TotalMilliseconds;
                _opStart = Time.realtimeSinceStartup;
                if (_op == IntPtr.Zero) throw new InvalidOperationException("the async build didn't start");
            }
            catch (Exception e)
            {
                _list.Clear();
                _op = IntPtr.Zero;
                ReportOnce("build", e);
                _refreshAt = now + 10f;
            }
        }

        private static void PollBuild()
        {
            bool done;
            try { done = FruitNavNative.IsDone(_op); }
            catch (Exception e) { ReportOnce("build poll", e); done = true; }
            if (!done) return;
            try { FruitNavNative.Release(_op); } catch { }
            _op = IntPtr.Zero;
            _list.Clear();
            _region = _building;
            Version++;
            LastBuild = $"build {Version}: {_opSources} sources in a {_region.size.x:0} x {_region.size.z:0} m box, " +
                        $"{(Time.realtimeSinceStartup - _opStart) * 1000f:0} ms, {_opMainMs:0.0} ms on the main thread";
            FruitLog.Info($"[FruitNav] {LastBuild}");
            try { Rebuilt?.Invoke(); } catch (Exception e) { ReportOnce("Rebuilt handler", e); }
        }

        private static void OnEvent(FruitEvent e)
        {
            // Blasts move props and hole walls; give the debris a moment to land.
            if (e != null && e.Kind == "Explosion") MarkDirty(2.5f);
        }

        internal static void ResetForScene()
        {
            if (_op != IntPtr.Zero) { try { FruitNavNative.Release(_op); } catch { } _op = IntPtr.Zero; }
            try { FruitNavNative.Destroy(_data); } catch { }
            _data = null;
            _list.Clear();
            Version = 0;
            _dirtyAt = -1f;
            _refreshAt = 0f;
            _lastStart = -100f;
            _region = default;
            _reported.Clear();
            _noted.Clear();
        }

        private static readonly HashSet<string> _reported = new HashSet<string>();

        private static void ReportOnce(string what, Exception e)
        {
            if (_reported.Add(what)) MelonLogger.Warning($"[FruitNav] {what} failed (reported once per scene): {e.Message}");
        }
    }
}
