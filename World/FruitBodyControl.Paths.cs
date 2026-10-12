using System;
using UnityEngine;

namespace FruitLib
{
    /// <summary>How a FruitBodyControl order ended. Since 5.19.0.</summary>
    public enum FruitMoveResult
    {
        /// <summary>Got there (or, for an attack, struck).</summary>
        Arrived,
        /// <summary>Kept walking into something and gave up after a few tries.</summary>
        Blocked,
        /// <summary>The navmesh has no way there: the goal is cut off.</summary>
        NoRoute,
        /// <summary>Its time ran out on the way. A plain Walk ends this way too.</summary>
        TimedOut,
        /// <summary>Stopped by a caller, or replaced by a new order.</summary>
        Stopped,
        /// <summary>The body it was going to is gone, or the walker died or passed out.</summary>
        Lost,
    }

    public static partial class FruitBodyControl
    {
        // ── Going to a point, and following paths ──
        //
        // Goto orders (GoTo, WalkTo, Approach, Follow, Attack, WalkAway) walk a FruitNav path once
        // a navmesh is loaded, and a straight line before that or when the body is off the mesh.
        //
        // Steering is pure pursuit: the body heads for a point a little way further along the path
        // than where it is, so it cuts no corners into walls and drifts back onto the line. Where
        // the path runs through a tight spot (a door, a gap between cars) that point comes closer
        // and the body walks rather than runs. With no progress for two seconds it backs off to one
        // side, finds the path again and has another go; after three goes the order ends Blocked.
        //
        // Since 5.21.0 (FruitLibConfig.NavSmoothWalking) the body is treated as what it is, a
        // ragdoll that sways a hand's width either way with every step and lags behind its heading:
        //
        //   - the path is shaped to keep FruitNav.PathMargin off the walls (FruitNav.Shape),
        //   - it steers from its centre (pelvis and torso) smoothed over about a step and led by its
        //     smoothed velocity, so it no longer chases each sway of the pelvis,
        //   - its lookahead grows with its speed,
        //   - it slows for corners and tight spots (the animation played slower, as a run plays it
        //     faster) and won't run into either,
        //   - at a corner sharper than PivotEnter it stops and turns on the spot before going on,
        //   - while it is still turning it walks an arc rather than crabbing sideways.
        //
        // Off, it walks as in 5.20: shortest path, steered from the pelvis. The pathing test runs
        // half its bodies each way.
        //
        // Since 5.22.0, with others about (smooth walking only):
        //
        //   - a goto order to a point that is taken gets a free spot near it (FruitBodyControl.Spots.cs),
        //   - a body standing or lying in the way is passed on the side the walker is on (Avoid),
        //   - at a door or in a passage, one of two bodies meeting waits for the other (GiveWay).

        /// <summary>Raised when a goto order ends, with how. Since 5.19.0.</summary>
        public static event Action<FruitBody, FruitMoveResult> MoveEnded;

        /// <summary>Bodies walking a path right now. FruitNav refreshes more often while there are any.</summary>
        public static int PathWalkers { get; private set; }

        /// <summary>Where a pursuing body heads: this far along the path ahead of it, metres (5.20 walking).</summary>
        public static float LookAhead = 1.5f;
        /// <summary>The same in a tight spot.</summary>
        public static float LookAheadTight = 0.6f;
        /// <summary>Room (beyond the agent radius) under which a stretch of path counts as tight.</summary>
        public static float TightRoom = 0.25f;

        /// <summary>Smooth walking: lookahead at a standstill, and how much it grows per m/s. Since 5.21.0.</summary>
        public static float LookAheadBase = 1.1f, LookAheadPerSpeed = 0.5f, LookAheadMax = 2.2f;
        /// <summary>Seconds the body's centre is smoothed over: about one step. Since 5.21.0.</summary>
        public static float CentreSmoothing = 0.3f;
        /// <summary>Walking pace (animation speed multiple) through tight spots, and round a 90 degree
        /// corner (sharper corners are taken pivoting). Since 5.21.0.</summary>
        public static float TightPace = 0.8f, CornerPace = 0.75f;
        /// <summary>Heading error, degrees, at which a body stops and turns on the spot, and at which it
        /// sets off again. Since 5.21.0.</summary>
        public static float PivotEnter = 75f, PivotExit = 20f;
        /// <summary>Most degrees off its facing a smoothly walking body steps. Since 5.21.0.</summary>
        public static float MaxSideStep = 40f;

        private const float ProgressWindow = 2f, ProgressNeeded = 0.3f, RecoverSeconds = 0.9f;
        private const int MaxRetries = 3;
        /// <summary>Path searches with shaping per frame for paths that are only stale (a few ms each).</summary>
        private const int ReplansPerFrame = 2;
        private static int _replans;

        /// <summary>
        /// Walks a body to a point, along a path when FruitNav is running, and stops within
        /// <paramref name="stopDistance"/> (flat metres). Since 5.19.0.
        /// </summary>
        public static bool GoTo(FruitBody body, Vector3 point, float stopDistance, float seconds, bool run = false)
            => GoTo(body, point, stopDistance, seconds, run, legacy: false);

        /// <summary>As above; <paramref name="legacy"/> walks it the 5.20 way, <paramref name="oldFooting"/>
        /// with the 5.23 footing (no grounder tuning, no small ledges), for the pathing test's A/B.</summary>
        internal static bool GoTo(FruitBody body, Vector3 point, float stopDistance, float seconds, bool run, bool legacy, bool oldFooting = false, Vector3? stance = null)
        {
            if (body == null || !body.Alive) return false;
            var o = OrderFor(body, seconds, run);
            o.Legacy = legacy;
            o.OldFooting = oldFooting;
            o.HasDest = true;
            o.Dest = point;
            o.StopDistance = Mathf.Max(0.3f, stopDistance);
            if (stance.HasValue)
            {
                // Chosen already (free, in reach of its item): the claim is the stance itself, and no
                // other spot takes its place on the way.
                o.Want = point; o.WantStop = o.StopDistance; o.Stand = stance.Value;
                o.Reslots = 0; o.ReslotAt = Time.time + 0.5f;
                o.Stance = true;
            }
            else Claim(o, point);   // a free spot, if the point is taken (FruitBodyControl.Spots.cs)
            Vector3 to = o.Dest - body.Position; to.y = 0f;
            o.Dir = to.sqrMagnitude > 1e-4f ? to.normalized : body.Facing;
            if (FruitNav.Ready)
            {
                o.Path ??= new FruitPath();
                FruitNav.FindPath(LiveCentre(body), o.Dest, o.Path, 3f, true, Smooth(o));
                o.PathAt = Time.time;
                o.PathGoal = o.Dest;
                o.Seg = 1;
            }
            return Bind(o);
        }

        /// <summary>The path a body is walking, or null (none, or walking a straight line). Since 5.19.0.</summary>
        public static FruitPath PathOf(FruitBody body)
            => body != null && _orders.TryGetValue(body.Id, out var o) && o.Path != null && o.Path.Found ? o.Path : null;

        /// <summary>Where a goto order is headed (its slot round the target body or the target's centre,
        /// or the point), if it has one. Since 5.19.0; the slot since 5.26.0.</summary>
        public static bool GoalOf(FruitBody body, out Vector3 goal)
        {
            goal = default;
            if (body == null || !_orders.TryGetValue(body.Id, out var o)) return false;
            if (o.Target != null) { goal = o.Slotted ? o.Slot : LiveCentre(o.Target); return true; }
            if (o.HasDest) { goal = o.Dest; return true; }
            return false;
        }

        /// <summary>A flee: a reachable point away from the threat when FruitNav can find one, else straight away.</summary>
        private static bool Flee(FruitBody body, Vector3 from, float seconds, bool run)
        {
            if (body == null || !body.Alive) return false;
            if (FruitNav.Ready)
            {
                var path = new FruitPath();
                float dist = Mathf.Clamp(seconds * (run ? 2.2f : 1.3f), 4f, 30f);
                // GoTo finds (and shapes) the path to the spot again.
                if (FruitNav.FindAway(body.Position, from, dist, path)) return GoTo(body, path.End, 0.8f, seconds, run);
            }
            return Walk(body, body.Position - from, seconds, run);
        }

        private static void ResetPath(Order o)
        {
            o.HasDest = false;
            o.Path?.Clear();
            o.PathAt = -100f;
            o.Seg = 1;
            o.Tight = false;
            o.Retries = 0;
            o.RecoverUntil = 0f;
            o.ProgressAt = Time.time + ProgressWindow;
            o.ProgressLeft = float.MaxValue;
            o.Result = FruitMoveResult.TimedOut;
            o.ComInit = false;
        }

        // ── The body's centre ──

        /// <summary>Pelvis and torso halfway, right now: what sways least of what is cheap to read.</summary>
        private static Vector3 RawCentre(FruitBody b)
        {
            var p = b.Limbs[FruitBodies.Pelvis];
            var t = b.Limbs[FruitBodies.Torso];
            if (p.Present && t.Present)
            {
                Vector3 a = p.LivePosition, c = t.LivePosition;
                a.x = (a.x + c.x) * 0.5f; a.y = (a.y + c.y) * 0.5f; a.z = (a.z + c.z) * 0.5f;
                return a;
            }
            return LiveCentre(b);
        }

        /// <summary>Smooths the body's centre and its velocity, every frame (first-order lag of <see cref="CentreSmoothing"/>).</summary>
        private static void TrackCentre(Order o, float dt)
        {
            Vector3 raw = RawCentre(o.Body);
            if (!o.ComInit || dt <= 0f || dt > 0.5f)
            {
                o.Com = raw; o.ComVel = Vector3.zero; o.ComInit = true;
                return;
            }
            float k = 1f - MathF.Exp(-dt / Math.Max(0.02f, CentreSmoothing));
            Vector3 c = o.Com, v = o.ComVel;
            float nx = c.x + (raw.x - c.x) * k, ny = c.y + (raw.y - c.y) * k, nz = c.z + (raw.z - c.z) * k;
            v.x += ((nx - c.x) / dt - v.x) * k;
            v.y += ((ny - c.y) / dt - v.y) * k;
            v.z += ((nz - c.z) / dt - v.z) * k;
            c.x = nx; c.y = ny; c.z = nz;
            o.Com = c; o.ComVel = v;
        }

        /// <summary>Where a smoothly walking body steers from: its smoothed centre, led by its smoothed
        /// velocity to make up the smoothing's lag.</summary>
        private static Vector3 SteerCentre(Order o)
        {
            Vector3 c = o.Com;
            c.x += o.ComVel.x * CentreSmoothing;
            c.z += o.ComVel.z * CentreSmoothing;
            return c;
        }

        private static float FlatSpeed(Order o) => MathF.Sqrt(o.ComVel.x * o.ComVel.x + o.ComVel.z * o.ComVel.z);

        // ── Following ──

        /// <summary>
        /// Where a point is on a path: the nearest point of the segments from a few behind
        /// <paramref name="seg"/> (a body that backed off or was shoved) to a dozen ahead (so a path
        /// that doubles back can't snap the body to its far side). Gives
        /// the segment, the distance along, how far off the line it is (positive = to the right of
        /// the direction of travel) and that direction.
        /// </summary>
        private static void Locate(FruitPath p, ref int seg, Vector3 me, out float along, out float off, out Vector3 tangent)
        {
            var c = p.Corners;
            int last = c.Count - 1;
            int lo = Math.Max(1, seg - 6), hi = Math.Min(last, seg + 12);
            float best = float.MaxValue, bestT = 0f;
            int bi = Math.Clamp(seg, 1, last);
            for (int i = lo; i <= hi; i++)
            {
                Vector3 a = c[i - 1], z = c[i];
                float dx = z.x - a.x, dz = z.z - a.z;
                float len2 = dx * dx + dz * dz;
                float t = len2 > 1e-8f ? Math.Clamp(((me.x - a.x) * dx + (me.z - a.z) * dz) / len2, 0f, 1f) : 1f;
                float px = a.x + dx * t - me.x, pz = a.z + dz * t - me.z;
                float d2 = px * px + pz * pz;
                if (d2 < best - 1e-6f) { best = d2; bi = i; bestT = t; }
            }
            seg = bi;
            Vector3 s0 = c[bi - 1], s1 = c[bi];
            float sx = s1.x - s0.x, sz = s1.z - s0.z;
            float sl = MathF.Sqrt(sx * sx + sz * sz);
            tangent = sl > 1e-4f ? new Vector3(sx / sl, 0f, sz / sl) : Vector3.forward;
            along = p.Along[bi - 1] + (p.Along[bi] - p.Along[bi - 1]) * bestT;
            float rx = me.x - (s0.x + sx * bestT), rz = me.z - (s0.z + sz * bestT);
            off = rx * tangent.z - rz * tangent.x;
        }

        /// <summary>
        /// Steers a goto order along its path. Sets <c>o.Dir</c> and <c>o.PaceWanted</c>; false when
        /// the order is over (<c>o.Result</c> says how).
        /// </summary>
        private static bool FollowPath(Order o, Vector3 goal, bool upright, float now)
        {
            var b = o.Body;
            bool smooth = Smooth(o);
            Vector3 me = smooth ? SteerCentre(o) : LiveCentre(b);
            o.Tight = false;

            if (now < o.RecoverUntil) { o.TrafficBlocked = false; o.Dir = o.RecoverDir; o.Pivot = false; return true; }

            if (!FruitNav.Ready) return Straight(o, goal, me);   // until there is a navmesh

            o.Path ??= new FruitPath();
            var p = o.Path;
            bool stale = p.Version != FruitNav.Version && p.Found;
            bool moved = FruitNav.Flat(goal - o.PathGoal) > (o.Target != null ? 1f : 0.5f) && now - o.PathAt > 0.5f;
            bool due = now - o.PathAt > (p.Found ? 6f : 1.5f);
            if (!p.Found && now - o.PathAt < 1.5f) return Straight(o, goal, me);   // no route just now: ask again soon
            // A path that is only out of date waits its turn: shaping costs a few ms.
            bool urgent = !p.Found || moved;
            if (urgent || ((stale || due) && _replans < ReplansPerFrame))
            {
                if (smooth) _replans++;
                FruitNav.FindPath(LiveCentre(b), goal, p, o.Target != null ? 2f : 3f, true, smooth);
                o.PathAt = now;
                o.PathGoal = goal;
                o.Seg = 1;
                if (o.Stats != null && p.Shaped) o.Stats.ShapeMs = Math.Max(o.Stats.ShapeMs, FruitNav.LastShapeMs);
                if (!p.Found) return Straight(o, goal, me);
            }

            var c = p.Corners;
            int last = c.Count - 1;
            int seg = o.Seg;
            Locate(p, ref seg, me, out float along, out float off, out Vector3 tangent);
            o.Seg = seg;
            float left = Mathf.Max(0f, p.Length - along);

            // Arrived at the end of the path, but not at the goal: it is cut off.
            float toEnd = FruitNav.Flat(c[last] - me);
            if (o.Seg == last && toEnd < 0.7f && p.Status == FruitPathStatus.Partial)
            {
                if (o.Keep) { Hold(o); return true; }
                o.Result = FruitMoveResult.NoRoute;
                return false;
            }

            o.Tight = p.RoomAhead(along, 2.5f) < TightRoom;
            o.InTight = o.Tight && p.RoomAhead(along, 0.01f) < TightRoom;
            float look;
            if (smooth) look = o.Tight ? LookAheadTight + 0.2f : Mathf.Clamp(LookAheadBase + LookAheadPerSpeed * FlatSpeed(o), LookAheadBase, LookAheadMax);
            else look = o.Tight ? LookAheadTight : LookAhead;
            bool wide = smooth && FruitLibConfig.NavWidthAware;
            if (wide) look = CutGuard(o, p, along, look, me);
            // Well off the line: rejoin it first.
            Vector3 aim = Mathf.Abs(off) > 0.7f ? p.PointAt(along + 0.5f) : p.PointAt(along + look);
            // A slot moves with its body: the last stretch heads for where it is now (FruitBodyControl.Spots.cs).
            if (o.Slotted && left < look) aim = goal;

            Vector3 dir = aim - me; dir.y = 0f;
            dir = dir.sqrMagnitude < 1e-4f ? tangent : dir.normalized;
            if (wide) dir = BackOntoMesh(me, dir);
            if (smooth)
            {
                // In a door or a passage nobody can step round anyone: one of two meeting waits.
                if (o.Tight)
                {
                    o.PassStage = 0;
                    if (now >= o.YieldUntil && upright && GiveWay(o, me, dir, now))
                    {
                        o.YieldUntil = now + YieldSeconds;
                        o.Yields++;
                        if (o.Stats != null) o.Stats.Yields++;
                    }
                }
                else
                {
                    // The body it is going to is walked round when its slot is round the far side.
                    var skip = o.Target;
                    if (o.Slotted && skip != null && FruitNav.Flat(LiveCentre(skip) - me) < FruitNav.Flat(goal - me)) skip = null;
                    // Keep the passing route until clear: the usual off-path correction otherwise
                    // pulls a walker back behind the same body as soon as it steps to one side.
                    if (!PassCrowd(o, skip, me, dir, goal, now, out dir))
                        dir = Avoid(b, skip, me, dir);
                }
            }
            else if (!o.Tight) dir = Separate(b, me, dir);
            if (smooth)
            {
                var skip = o.Target;
                if (o.Slotted && skip != null && FruitNav.Flat(LiveCentre(skip) - me) < FruitNav.Flat(goal - me)) skip = null;
                dir = Traffic(o, skip, me, dir, now);
            }
            else o.TrafficBlocked = false;
            o.Dir = dir;

            // A passing leg may go sideways while the original path's distance stays constant.
            // Measure the route actually being walked so progress recovery does not cancel it.
            if (o.PassStage != 0)
                left = FruitNav.Flat((o.PassStage == 1 ? o.PassBeside : o.PassBeyond) - me) +
                    (o.PassStage == 1 ? FruitNav.Flat(o.PassBeyond - o.PassBeside) : 0f) + FruitNav.Flat(goal - o.PassBeyond);

            if (smooth) Govern(o, p, along, upright, now);
            else o.PaceWanted = o.Run && !o.Tight ? RunAnimationSpeed : 1f;
            if (o.TrafficBlocked) o.Pivot = false;

            // Progress: the way left has to shrink. Down, pivoting or waiting bodies get time.
            if (!upright || o.Paused || o.Pivot || now < o.YieldUntil ||
                (o.TrafficBlocked && now - o.TrafficSince < 3f)) o.ProgressAt = now + ProgressWindow;
            else if (now >= o.ProgressAt)
            {
                if (o.ProgressLeft - left < ProgressNeeded)
                {
                    if (CloseEnough(o, LiveCentre(b))) return Arrive(o);   // FruitBodyControl.Spots.cs
                    if (++o.Retries > MaxRetries) { o.Result = FruitMoveResult.Blocked; return false; }
                    // Back off and to one side (alternating), then find the path again.
                    float side = (o.Retries & 1) == 1 ? 40f : -40f;
                    o.RecoverDir = Quaternion.AngleAxis(180f + side, Vector3.up) * dir;
                    o.RecoverUntil = now + RecoverSeconds;
                    o.TrafficBlocked = false;
                    o.PathAt = -100f;
                    o.Pivot = false;
                    if (o.Stats != null) o.Stats.Retries++;
                    Vector3 at = LiveCentre(b);
                    FruitLog.Info($"[FruitBodyControl] #{b.Id} stuck {left:0.0} m short at ({at.x:0.0}, {at.y:0.0}, {at.z:0.0}){Against(at, dir)}; backing off (try {o.Retries}/{MaxRetries}).");
                }
                else if (o.ProgressLeft - left > 1f) o.Retries = 0;
                o.ProgressLeft = left;
                o.ProgressAt = now + ProgressWindow + (now < o.RecoverUntil ? RecoverSeconds : 0f);
            }
            return true;
        }

        // ── Width-aware steering (5.33.0) ──
        //
        // The navmesh keeps its edge AgentRadius (0.38 m) from every wall, and the shaped path keeps
        // PathMargin beyond that where there is room. Pure pursuit then heads for a point
        // a lookahead along the path, and that chord cuts every bend: round a 90 degree corner with a
        // 1.6 m lookahead it passes about half a metre inside the path, which is where the
        // path's margin was, and a body 0.45 m across (a shoulder is 0.22 m off its centre) ends up
        // with a shoulder on the wall. So the lookahead is shortened until the chord stays within the
        // room the path has there; and a body whose smoothed centre has swayed out of the walkable
        // area (inside the 0.38 m, shoulders about to catch) is pushed back toward it.

        /// <summary>Deepest a chord may cut a bend even in the open, metres; least in the tightest spot;
        /// and what is kept back from the path's room (its sway). Since 5.33.0.</summary>
        public static float MaxCut = 0.4f, MinCut = 0.06f, CutSlack = 0.05f;
        /// <summary>The shortest lookahead the guard leaves, metres, and how fast a shortened one grows back, m/s.</summary>
        public static float MinLookAhead = 0.5f, LookRegrow = 1.5f;

        /// <summary>
        /// The lookahead for this frame: <paramref name="look"/>, or less where the chord from the
        /// body to the path point that far ahead would pass more than the room allows from the path
        /// itself (the walk-in's offset counts too: a body well off the line heads for a nearer point
        /// and rejoins it). Shortened at once, lengthened at <see cref="LookRegrow"/> so the aim
        /// doesn't jump about.
        /// </summary>
        private static float CutGuard(Order o, FruitPath p, float along, float look, Vector3 me)
        {
            float use = look;
            for (int iter = 0; iter < 6 && use > MinLookAhead; iter++)
            {
                Vector3 aim = p.PointAt(along + use);
                float cx = aim.x - me.x, cz = aim.z - me.z;
                float len = MathF.Sqrt(cx * cx + cz * cz);
                if (len < 0.2f) break;
                float nx = cz / len, nz = -cx / len;   // the chord's normal
                float cut = 0f;
                for (int k = 1; k <= 4; k++)
                {
                    Vector3 s = p.PointAt(along + use * k * 0.2f);
                    float d = Math.Abs((s.x - me.x) * nx + (s.z - me.z) * nz);
                    if (d > cut) cut = d;
                }
                float room = p.RoomAhead(along, use);
                float allowed = float.IsPositiveInfinity(room) ? MaxCut : Mathf.Clamp(room - CutSlack, MinCut, MaxCut);
                if (cut <= allowed) break;
                use = Math.Max(MinLookAhead, use * 0.75f);
            }
            float last = o.LookUse;
            if (last > 0f && use > last) use = Math.Min(use, last + LookRegrow * Time.deltaTime);
            o.LookUse = use;
            return use;
        }

        /// <summary>A little push of the heading back toward the walkable area when the (smoothed)
        /// centre of the body is outside it by more than a few centimetres.</summary>
        private static Vector3 BackOntoMesh(Vector3 me, Vector3 dir)
        {
            if (!FruitNav.Under(me, 0.5f, out Vector3 q)) return dir;
            float ox = q.x - me.x, oz = q.z - me.z;
            float m = MathF.Sqrt(ox * ox + oz * oz);
            if (m < 0.04f) return dir;
            float w = Math.Min(1f, m / 0.15f) * 0.9f;
            float vx = dir.x + ox / m * w, vz = dir.z + oz / m * w;
            float n = MathF.Sqrt(vx * vx + vz * vz);
            return n > 1e-4f ? new Vector3(vx / n, 0f, vz / n) : dir;
        }

        /// <summary>
        /// Smooth walking's pace and pivots: slower for a corner coming up, a tight spot or a heading
        /// it hasn't turned to yet; no running into any of them; a stop to turn on the spot when
        /// the heading is more than <see cref="PivotEnter"/> off.
        /// </summary>
        private static void Govern(Order o, FruitPath p, float along, bool upright, float now)
        {
            // How far the path turns over the next couple of metres.
            Vector3 t0 = p.DirectionAt(along + 0.2f), t1 = p.DirectionAt(along + 2.5f);
            float cos = Math.Clamp(t0.x * t1.x + t0.z * t1.z, -1f, 1f);
            float turn = MathF.Acos(cos) * Mathf.Rad2Deg;

            float desired = MathF.Atan2(o.Dir.x, o.Dir.z) * Mathf.Rad2Deg;
            float err = Mathf.Abs(Mathf.DeltaAngle(o.Runtime.CreatureYRotation, desired));

            float pace = o.Run && !o.Tight && turn < 30f && err < 30f ? RunAnimationSpeed : 1f;
            if (o.Tight) pace = Math.Min(pace, TightPace);
            if (turn > 35f) pace = Math.Min(pace, 1f + (CornerPace - 1f) * Math.Clamp((turn - 35f) / 55f, 0f, 1f));
            if (err > 30f) pace = Math.Min(pace, TightPace);
            o.PaceWanted = pace;
            o.TurnAhead = turn;
            o.HeadingError = err;

            if (!o.Pivot)
            {
                if (upright && err > PivotEnter) { o.Pivot = true; o.PivotSince = now; o.Pivots++; }
            }
            else if (err < PivotExit || now - o.PivotSince > 2.5f || !upright) o.Pivot = false;
        }

        /// <summary>One frame of a pivot: the walk let go, the target yaw turned toward the heading,
        /// as a standing turn (FruitBodyControl.Turn.cs) does.</summary>
        private static void TickPivot(Order o, float dt)
        {
            var rt = o.Runtime;
            float desired = MathF.Atan2(o.Dir.x, o.Dir.z) * Mathf.Rad2Deg;
            if (StepTurns && o.Walk != null && TrafficClear(o.Body, o.Target, LiveCentre(o.Body), o.Body.Facing, 1.5f))
            {
                StepTurnTick(o, desired, TurnRate * 1.5f, dt, Time.time);
                return;
            }
            if (o.Requested) Release(o);
            o.StepTurning = false;
            rt.UpdateTargetYRot(SteerYaw(o, desired, TurnRate * 1.5f, dt));
        }

        /// <summary>", against X" for what is right in front of a stuck body: a ledge at its feet
        /// (with its height; since 5.24.0), or something at knee, hip or chest height.</summary>
        private static string Against(Vector3 pelvis, Vector3 dir)
        {
            if (Physics.Raycast(pelvis, Vector3.down, out RaycastHit fl, 1.6f, FruitNav.Layers, QueryTriggerInteraction.Ignore))
            {
                float fy = fl.point.y;
                foreach (float up in new[] { 0.015f, 0.05f, 0.12f })
                {
                    Vector3 from = new Vector3(pelvis.x, fy + up, pelvis.z);
                    if (!Physics.Raycast(from, dir, out RaycastHit h, 0.7f, FruitNav.Layers, QueryTriggerInteraction.Ignore) || h.normal.y > 0.6f) continue;
                    Vector3 over = h.point + dir * 0.06f;
                    over.y = fy + 0.5f;
                    string tall = Physics.Raycast(over, Vector3.down, out RaycastHit top, 0.7f, FruitNav.Layers, QueryTriggerInteraction.Ignore)
                        ? $"{(top.point.y - fy) * 100f:0} cm" : "tall";
                    return $", against a {tall} ledge (\"{(h.collider != null ? h.collider.name : "?")}\") {h.distance:0.00} m ahead of its feet";
                }
            }
            foreach (float dy in new[] { -0.5f, 0f, 0.4f })
            {
                Vector3 from = pelvis; from.y += dy;
                if (Physics.Raycast(from, dir, out RaycastHit h, 0.9f, ~((1 << 6) | (1 << 7)), QueryTriggerInteraction.Ignore) && h.collider != null)
                    return $", against \"{h.collider.name}\" (layer {h.collider.gameObject.layer}) {h.distance:0.00} m ahead at {dy + 1f:0.0} m";
            }
            // Nothing ahead on the centre line: a shoulder or an arm may still be caught on something
            // to the side (a door frame, a wall corner). 5.33.0.
            string beside = null;
            float bestSide = 0.62f;
            foreach (float yaw in new[] { 40f, -40f, 90f, -90f, 135f, -135f })
            {
                Vector3 side = Quaternion.AngleAxis(yaw, Vector3.up) * dir;
                foreach (float dy in new[] { 0f, 0.4f })
                {
                    Vector3 from = pelvis; from.y += dy;
                    if (!Physics.Raycast(from, side, out RaycastHit h, 0.62f, ~((1 << 6) | (1 << 7)), QueryTriggerInteraction.Ignore) || h.collider == null) continue;
                    if (h.distance >= bestSide) continue;
                    bestSide = h.distance;
                    beside = $", beside it: \"{h.collider.name}\" (layer {h.collider.gameObject.layer}) {h.distance:0.00} m to its {(yaw > 0f ? "right" : "left")}{(Math.Abs(yaw) > 100f ? " rear" : "")} at {dy + 1f:0.0} m";
                }
            }
            return beside ?? ", nothing in front";
        }

        /// <summary>Heads straight for the goal (off the navmesh, or none yet).</summary>
        private static bool Straight(Order o, Vector3 goal, Vector3 me)
        {
            Vector3 to = goal - me; to.y = 0f;
            if (to.sqrMagnitude > 1e-4f) o.Dir = to.normalized;
            if (Smooth(o)) o.Dir = Traffic(o, o.Target, me, o.Dir, Time.time);
            else o.TrafficBlocked = false;
            o.Pivot = false;
            return true;
        }

        /// <summary>Seconds a body waits to let another through a tight spot before looking again. Since 5.22.0.</summary>
        public static float YieldSeconds = 1.2f;

        /// <summary>
        /// Whether a body about to go through a tight spot should wait: another walker is coming the
        /// other way within a couple of metres ahead, and either it is in the tight spot already or
        /// neither is and it has the lower id (a fixed order, so two never both wait). A body already
        /// in it keeps going: stopping there would only block the door.
        /// </summary>
        private static bool GiveWay(Order o, Vector3 me, Vector3 dir, float now)
        {
            if (o.InTight) return false;
            foreach (var other in _orders.Values)
            {
                if (other == o || !other.Requested || other.Paused || now < other.YieldUntil || !other.ComInit) continue;
                Vector3 c = other.Com;
                float rx = c.x - me.x, rz = c.z - me.z;
                if (Math.Abs(c.y - me.y) > 1.6f) continue;
                float fwd = rx * dir.x + rz * dir.z, lat = Math.Abs(rx * dir.z - rz * dir.x);
                if (fwd < 0.3f || fwd > 2.8f || lat > 1f) continue;
                if (other.Dir.x * dir.x + other.Dir.z * dir.z > -0.3f) continue;   // not coming this way
                if (other.InTight || other.Body.Id < o.Body.Id) return true;
            }
            return false;
        }

        /// <summary>
        /// Smooth walking's way round other bodies: a body standing or lying in the way within two
        /// metres ahead is passed on the side the walker is already on (or, dead ahead, to its right,
        /// so two walking into each other pass), harder the closer it is; and anyone within arm's
        /// length pushes it off a little, as in <see cref="Separate"/>. The body it is going to is
        /// left out.
        /// </summary>
        private static Vector3 Avoid(FruitBody self, FruitBody target, Vector3 me, Vector3 dir)
        {
            float sx = 0f, sz = 0f, px = 0f, pz = 0f;
            float rightX = dir.z, rightZ = -dir.x;
            var all = FruitBodies.All;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == self || b == target || !b.HasPosition || Math.Abs(b.Position.y - me.y) > 1.6f) continue;
                float rx = b.Position.x - me.x, rz = b.Position.z - me.z;
                float d2 = rx * rx + rz * rz;
                if (d2 > 6.25f || d2 < 1e-4f) continue;
                float fwd = rx * dir.x + rz * dir.z, lat = rx * rightX + rz * rightZ;   // lat > 0: it is to the right
                bool down = !Upright(b);
                float lane = down ? 1.0f : 0.8f;
                if (fwd > 0.15f && fwd < 2f && Math.Abs(lat) < lane)
                {
                    float side = lat > 0.05f ? -1f : 1f;
                    float w = (1f - fwd / 2f) * (lane - Math.Abs(lat)) / lane;
                    sx += rightX * side * w; sz += rightZ * side * w;
                }
                if (d2 < 0.81f && fwd > -0.2f)
                {
                    float d = MathF.Sqrt(d2), w = (0.9f - d) / 0.9f;
                    px -= rx / d * w; pz -= rz / d * w;
                }
            }
            if (sx * sx + sz * sz + px * px + pz * pz < 1e-6f) return dir;
            float vx = dir.x + sx * 1.1f + px * 0.6f, vz = dir.z + sz * 1.1f + pz * 0.6f;
            float m = MathF.Sqrt(vx * vx + vz * vz);
            return m > 1e-4f ? new Vector3(vx / m, 0f, vz / m) : dir;
        }

        /// <summary>A little sideways nudge away from other bodies just ahead, so a crowd doesn't walk into itself (5.20 walking).</summary>
        private static Vector3 Separate(FruitBody self, Vector3 me, Vector3 dir)
        {
            Vector3 push = Vector3.zero;
            var all = FruitBodies.All;
            for (int i = 0; i < all.Count; i++)
            {
                var o = all[i];
                if (o == self || !o.HasPosition) continue;
                float dx = o.Position.x - me.x, dz = o.Position.z - me.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > 0.81f || d2 < 1e-4f) continue;
                if (dx * dir.x + dz * dir.z < 0f) continue;   // behind
                float d = Mathf.Sqrt(d2);
                float w = (0.9f - d) / 0.9f;
                push.x -= dx / d * w; push.z -= dz / d * w;
            }
            if (push.sqrMagnitude < 1e-4f) return dir;
            Vector3 v = dir + push * 0.6f; v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : dir;
        }

        private static void Ended(FruitBody b, FruitMoveResult result)
        {
            if (MoveEnded == null) return;
            try { MoveEnded(b, result); }
            catch (Exception e) { FruitBodies.ReportOnce("MoveEnded handler", e); }
        }
    }
}
