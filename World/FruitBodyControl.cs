using System;
using System.Collections.Generic;
using Il2CppLVA.Puppeteers.Variants.Humanoid;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>A blow one body deals another. Since 5.14.0.</summary>
    public enum FruitStrike
    {
        /// <summary>A fist to the head (or the torso): a sharp knock and a bruise.</summary>
        Punch,
        /// <summary>Both hands into the chest: a big push that can knock the other off balance.</summary>
        Shove,
    }

    /// <summary>
    /// Makes a body walk, run, go to another body and hit it, through the game's own locomotion:
    /// the same walk request the game's test walking uses (WalkInteraction.AddWalkRequest),
    /// steered by the puppeteer's target yaw and move direction. The body walks with its own
    /// animation, balance and stumbling; a body that is down gets up first by itself, then walks.
    ///
    /// <code>
    /// FruitBodyControl.Walk(body, awayFromThreat, seconds: 4f, run: true);
    /// FruitBodyControl.Follow(body, friend, stopDistance: 1.5f, seconds: 20f);
    /// FruitBodyControl.Attack(body, enemy, FruitStrike.Punch, seconds: 10f, run: true, source: "MyMod");
    /// </code>
    ///
    /// How the game steers (Release, Ghidra 2026-10-10): AddWalkRequest sets
    /// RuntimeData.TargetMoveDirection to local forward and raises OnWalk; the move state animates
    /// along TargetMoveDirection; DynamicYTargetRotation sets PuppeteerTargetYRot to the pelvis's
    /// yaw every Update, so on its own a body walks whichever way it faces. Steering is written
    /// in LateUpdate, after that, and read by the puppeteer in the next FixedUpdate.
    ///
    /// Running: the game has no run. Its locomotion blend tree is Idle plus eight Moving_*
    /// clips at walking pace, and HumanoidMoveControl rewrites the clip-speed parameter
    /// (moveSpeed) every FixedUpdate. A run here is the puppeteer's Animator played faster
    /// (<see cref="RunAnimationSpeed"/>) while the body is on its feet: quicker steps, which the
    /// physical body follows as well as its balance lets it.
    ///
    /// Hitting: the game has no melee either; see FruitBodyControl.Strikes.cs.
    /// </summary>
    /// <remarks>Since 5.13.0. Running, following, approaching and strikes since 5.14.0; strikes reworked in 5.15.0.</remarks>
    public static partial class FruitBodyControl
    {
        private sealed class Order
        {
            public FruitBody Body;
            public Vector3   Dir;
            public float     Until;
            public bool      Requested;
            public WalkInteraction Walk;
            public HumanoidPuppeteerRuntimeData Runtime;
            public Animator  Animator;
            public IntPtr    BoundTo;

            public bool      Run;
            /// <summary>Animator speed multiple applied now (1 = the game's own): a run, or slowed for a corner.</summary>
            public float     Pace = 1f;
            /// <summary>What this frame's steering asks for.</summary>
            public float     PaceWanted = 1f;
            public float     SpeedBefore = 1f;

            /// <summary>Walks the 5.20 way: shortest path, steered from the pelvis (the pathing test's A/B).</summary>
            public bool      Legacy;
            // Where the body is on average (pelvis and torso, smoothed over about a step), and how it moves.
            public Vector3   Com, ComVel;
            public bool      ComInit;
            // Turning on the spot at a sharp corner.
            public bool      Pivot;
            public float     PivotSince;
            public int       Pivots;
            public float     TurnAhead, HeadingError;
            public WalkStats Stats;
            // Spreading out (FruitBodyControl.Spots.cs): the point asked for and how close, where the
            // body will stand (its claim), and how to look for another spot near it.
            public Vector3   Want, Stand;
            public float     WantStop;
            public float     SpreadRadius, ReslotAt;
            public FruitPlace SpreadPlace;
            public int       Reslots;
            // Giving way in a tight spot.
            public float     YieldUntil;
            public bool      TrafficBlocked;
            public float     TrafficSince;
            // Footing (FruitBodyControl.Footing.cs): the rig's grounder, and the pathing test's A/B.
            public Footing   Feet;
            public bool      OldFooting;
            public bool      InTight;
            public int       Yields;
            /// <summary>A walk to a stance beside an item (FruitBodyControl.Hands.cs): its claim is the
            /// stance, it doesn't spread out to a free spot and doesn't turn to the group on arrival.</summary>
            public bool      Stance;
            /// <summary>The lookahead used last frame, so the width-aware guard can ease it back out.</summary>
            public float     LookUse;

            // Going to a body: re-aimed at its live position every frame.
            public FruitBody Target;
            public float     StopDistance;
            /// <summary>Follow: stay with it. Approach: done on arrival.</summary>
            public bool      Keep;
            /// <summary>Close enough: standing by until the target moves off.</summary>
            public bool      Paused;
            // Its slot round the target (FruitBodyControl.Spots.cs): the bearing from the target's
            // centre, where that is right now, and when to look at it again; turning to the target.
            public bool      Slotted, Watching;
            public float     SlotAngle, SlotAt, HoldDistance;
            public Vector3   Slot;

            // Attack: strike on arrival.
            public bool        Strikes;
            public FruitStrike Kind;
            public string      Source;
            public float       Strength;

            // Turning on the spot (FruitBodyControl.Turn.cs).
            public bool  Turn, TurnStepping;
            public float TurnFrom, TurnSweep, TurnStart, TurnTime, TurnCheckYaw;

            // Going to a point, and paths (FruitBodyControl.Paths.cs).
            public bool      HasDest;
            public Vector3   Dest;
            public FruitPath Path;
            public float     PathAt;
            public Vector3   PathGoal;
            public int       Seg;
            public bool      Tight;
            public int       Retries;
            public float     RecoverUntil, ProgressAt, ProgressLeft;
            public Vector3   RecoverDir;
            // A short route round a stationary body, retained while passing it.
            public Vector3   PassBeside, PassBeyond;
            public int       PassStage;
            public float     PassUntil, PassCheckAt;
            public FruitMoveResult Result;

            // Only a walk request, nothing steering it (the gait probe's baseline), and the yaw
            // command kept between frames while PersistentYaw is on.
            public bool      Raw;
            public bool      YawCmdOn;
            public float     YawCmd;
            /// <summary>When the walk request went in (FruitBodyControl.Gait.cs: the start ramp).</summary>
            public float     StartAt;
            /// <summary>Turning with steps right now (FruitBodyControl.Turn.cs, StepTurns).</summary>
            public bool      StepTurning;

            // Step assist (FruitBodyControl.Steps.cs).
            public readonly Rigidbody[] Rbs = new Rigidbody[FruitBodies.LimbCount];
            public IntPtr    RbsFor;
            public float     StepUntil, StepStart, StepRise, StepH, StepPelvisY0;
            public int       StepFoot = -1;
            public float     LeftFootReady, RightFootReady;
        }

        private static readonly Dictionary<int, Order> _orders = new Dictionary<int, Order>();
        private static readonly List<int> _done = new List<int>();
        private static Il2CppSystem.Object _holder;
        private static bool _yawLogged;

        /// <summary>Degrees per second a walking body turns toward its heading.</summary>
        public static float TurnRate = 150f;

        /// <summary>Animator playback speed while running, as a multiple of normal. Since 5.14.0.</summary>
        public static float RunAnimationSpeed = 1.6f;

        // ── Walking ──

        /// <summary>Walks a body along a world direction (y ignored) for a while. Calling it again
        /// changes the heading and the time left. Returns false if the body can't be driven.</summary>
        public static bool Walk(FruitBody body, Vector3 worldDirection, float seconds)
            => Walk(body, worldDirection, seconds, false);

        /// <summary>Walks, or runs, a body along a world direction (y ignored). Since 5.14.0.</summary>
        public static bool Walk(FruitBody body, Vector3 worldDirection, float seconds, bool run)
        {
            if (body == null || !body.Alive) return false;
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude < 1e-4f) return false;

            var o = OrderFor(body, seconds, run);
            o.Dir = worldDirection.normalized;
            return Bind(o);
        }

        /// <summary>Walks a body away from a point: to a reachable spot away from it when FruitNav
        /// is running (5.19.0), straight away otherwise.</summary>
        public static bool WalkAway(FruitBody body, Vector3 from, float seconds)
            => Flee(body, from, seconds, false);

        /// <summary>Walks, or runs, a body away from a point. Since 5.14.0.</summary>
        public static bool WalkAway(FruitBody body, Vector3 from, float seconds, bool run)
            => Flee(body, from, seconds, run);

        /// <summary>Walks a body to a point and stops within a metre of it (since 5.19.0; before,
        /// it walked that way until the time ran out). Same as <see cref="GoTo"/>.</summary>
        public static bool WalkTo(FruitBody body, Vector3 to, float seconds)
            => GoTo(body, to, 1f, seconds, false);

        /// <summary>Walks, or runs, a body to a point. Since 5.14.0.</summary>
        public static bool WalkTo(FruitBody body, Vector3 to, float seconds, bool run)
            => GoTo(body, to, 1f, seconds, run);

        /// <summary>
        /// Walks a body up to another one and stops within <paramref name="stopDistance"/> of it
        /// (flat metres), re-aiming at where the other is every frame. The order ends on arrival
        /// or after <paramref name="seconds"/>. Since 5.14.0.
        /// </summary>
        public static bool Approach(FruitBody body, FruitBody target, float stopDistance, float seconds, bool run = false)
            => GoTo(body, target, stopDistance, seconds, run, keep: false);

        /// <summary>
        /// Like <see cref="Approach"/>, but it stays with the target for the whole time: it stands
        /// by while close, and sets off again when the other moves more than half a metre past
        /// <paramref name="stopDistance"/>. Since 5.14.0.
        /// </summary>
        public static bool Follow(FruitBody body, FruitBody target, float stopDistance, float seconds, bool run = false)
            => GoTo(body, target, stopDistance, seconds, run, keep: true);

        /// <summary>
        /// Goes up to another body and hits it once on arrival (or straight away if it is already
        /// in <see cref="Reach"/>). The blow is published as an "Attacked" event from
        /// <paramref name="source"/>. <paramref name="strength"/> scales the impulse and the bruise.
        /// Since 5.14.0.
        /// </summary>
        public static bool Attack(FruitBody attacker, FruitBody victim, FruitStrike kind, float seconds,
                                  bool run = false, string source = "FruitLib", float strength = 1f)
        {
            if (attacker == null || victim == null || attacker == victim) return false;
            bool bound = GoTo(attacker, victim, Reach * 0.8f, seconds, run, keep: false);
            // An order that could not bind yet stays queued and retries, so it must carry the strike either way.
            if (!_orders.TryGetValue(attacker.Id, out var o) || o.Target != victim) return false;
            o.Strikes = true;
            o.Kind = kind;
            o.Source = string.IsNullOrEmpty(source) ? "FruitLib" : source;
            o.Strength = Mathf.Max(0f, strength);
            return bound;
        }

        public static void Stop(FruitBody body)
        {
            if (body != null && _orders.TryGetValue(body.Id, out var o))
            {
                Release(o);
                _orders.Remove(body.Id);
                if (IsGoto(o)) { o.Result = FruitMoveResult.Stopped; Summarise(o); Ended(body, FruitMoveResult.Stopped); }
            }
        }

        private static bool IsGoto(Order o) => o.Target != null || o.HasDest;

        public static bool IsWalking(FruitBody body) => body != null && _orders.ContainsKey(body.Id);

        /// <summary>Whether a body has been told to run (it only does while on its feet). Since 5.14.0.</summary>
        public static bool IsRunning(FruitBody body) => body != null && _orders.TryGetValue(body.Id, out var o) && o.Run;

        /// <summary>The body a walking body is going to, following or about to hit, or null. Since 5.14.0.</summary>
        public static FruitBody TargetOf(FruitBody body) => body != null && _orders.TryGetValue(body.Id, out var o) ? o.Target : null;

        /// <summary>Whether a body is on its way to hit another. Since 5.14.0.</summary>
        public static bool IsAttacking(FruitBody body)
            => body != null && ((_orders.TryGetValue(body.Id, out var o) && o.Strikes) || Swinging(body));

        /// <summary>Where a walking body is headed, or zero.</summary>
        public static Vector3 Heading(FruitBody body) => body != null && _orders.TryGetValue(body.Id, out var o) ? o.Dir : Vector3.zero;

        private static Order OrderFor(FruitBody body, float seconds, bool run)
        {
            FruitPose.YieldToWalk(body);
            if (!_orders.TryGetValue(body.Id, out var o)) _orders[body.Id] = o = new Order { Body = body };
            else if (IsGoto(o)) { o.Result = FruitMoveResult.Stopped; Summarise(o); Ended(body, FruitMoveResult.Stopped); }   // replaced by this one
            ResetPath(o);
            o.Until = Time.time + Mathf.Max(0.2f, seconds);
            o.Run = run;
            o.Target = null; o.Paused = false; o.Keep = false; o.Strikes = false;
            o.Slotted = false; o.Watching = false; o.SlotAt = 0f;
            o.Legacy = false; o.Pivot = false; o.Pivots = 0; o.Stats = null;
            o.SpreadPlace = null; o.Reslots = 0; o.YieldUntil = 0f; o.Yields = 0; o.OldFooting = false;
            o.Stance = false; o.LookUse = 0f;
            o.PassStage = 0; o.PassCheckAt = 0f;
            o.TrafficBlocked = false;
            o.Raw = false; o.YawCmdOn = false;
            if (o.Turn) { o.Turn = false; Release(o); }
            return o;
        }

        /// <summary>
        /// Keeps each walking body's yaw command between frames instead of starting it again from
        /// where the pelvis is. The game's DynamicYTargetRotation sets PuppeteerTargetYRot to the
        /// pelvis's own yaw every Update, so a command made as "turn rate x frame time from the
        /// target" is only ever that far ahead of the body: a degree or two at a high frame rate,
        /// and the pelvis's turning torque follows the frame rate. With this on the command runs on
        /// at <see cref="TurnRate"/> and stays up to <see cref="YawLead"/> degrees ahead of the
        /// body. Off by default until the gait probe says it is better. Since 5.32.0.
        /// </summary>
        public static bool PersistentYaw = false;

        /// <summary>Furthest a persistent yaw command runs ahead of the body, degrees. Since 5.32.0.</summary>
        public static float YawLead = 30f;

        /// <summary>The yaw to command this frame for a heading: <paramref name="rate"/> degrees
        /// a second toward <paramref name="desired"/>, from the last command (PersistentYaw) or
        /// from the target as it stands now.</summary>
        private static float SteerYaw(Order o, float desired, float rate, float dt)
        {
            var rt = o.Runtime;
            if (!PersistentYaw)
            {
                o.YawCmdOn = false;
                return Mathf.MoveTowardsAngle(rt.PuppeteerTargetYRot, desired, rate * dt);
            }
            float facing = rt.CreatureYRotation;
            if (!o.YawCmdOn) { o.YawCmd = facing; o.YawCmdOn = true; }
            o.YawCmd = Mathf.MoveTowardsAngle(o.YawCmd, desired, rate * dt);
            float lead = Mathf.DeltaAngle(facing, o.YawCmd);
            if (lead > YawLead) o.YawCmd = facing + YawLead;
            else if (lead < -YawLead) o.YawCmd = facing - YawLead;
            return o.YawCmd;
        }

        /// <summary>
        /// A walk request and nothing else: no heading, no steering, the game's own walk straight
        /// ahead along wherever the body faces. The gait probe's baseline; end it with
        /// <see cref="Stop"/>. Since 5.32.0.
        /// </summary>
        internal static bool RawWalk(FruitBody body, float seconds)
        {
            if (body == null || !body.Alive) return false;
            var o = OrderFor(body, seconds, false);
            o.Raw = true;
            o.Dir = body.Facing;
            return Bind(o);
        }

        private static bool GoTo(FruitBody body, FruitBody target, float stopDistance, float seconds, bool run, bool keep)
        {
            if (body == null || !body.Alive || target == null || target == body || !target.HasPosition) return false;
            var o = OrderFor(body, seconds, run);
            o.Target = target;
            o.StopDistance = Mathf.Max(0.3f, stopDistance);
            o.Keep = keep;
            Vector3 to = target.Position - body.Position;
            to.y = 0f;
            o.Dir = to.sqrMagnitude > 1e-4f ? to.normalized : body.Facing;
            return Bind(o);
        }

        private static bool Bind(Order o)
        {
            var b = o.Body;
            if (o.BoundTo == b.CreaturePtr && o.Walk != null) return true;
            Release(o);
            o.Walk = null; o.Runtime = null; o.Animator = null; o.BoundTo = b.CreaturePtr;

            try
            {
                var refs = b.Bind.Refs;
                o.Runtime = b.Bind.Runtime;
                var map = refs?.ExternalInteractions?.m_interactions;
                if (map != null)
                    foreach (var i in map.Values)
                        if (i != null && FruitBodies.TypeNameOf(i) == "WalkInteraction") { o.Walk = i.TryCast<WalkInteraction>(); break; }
            }
            catch (Exception e) { FruitBodies.ReportOnce("walk interaction", e); }

            try { o.Animator = b.Bind.Refs?.Animation?.Animator; }
            catch (Exception e) { FruitBodies.ReportOnce("puppeteer animator", e); }

            if (o.Walk == null || o.Runtime == null)
            {
                FruitLog.Info($"[FruitBodyControl] #{b.Id} has no walk interaction yet.");
                return false;
            }
            return true;
        }

        private static void Release(Order o)
        {
            SetPace(o, 1f);
            RestoreFooting(o);
            o.YawCmdOn = false;
            if (!o.Requested) return;
            o.Requested = false;
            try
            {
                if (o.Walk != null && !o.Walk.WasCollected && o.Walk.HoldsWalkRequest(Holder)) o.Walk.RemoveWalkRequest(Holder);
                if (o.Runtime != null && !o.Runtime.WasCollected) o.Runtime.SetTargetMoveDirection(Vector3.zero);
            }
            catch (Exception e) { FruitBodies.ReportOnce("walk release", e); }
        }

        /// <summary>Plays the body's animation faster or slower than its own speed (1 = back to its own):
        /// a run, or a slower walk into a corner. Only while it is on its feet: stand-up and agony
        /// animations run at the game's speed. Slower than 1 since 5.21.0.</summary>
        private static void SetPace(Order o, float pace)
        {
            pace = Mathf.Clamp(pace, 0.5f, 3f);
            if (Mathf.Abs(pace - 1f) < 0.01f) pace = 1f;
            if (pace == o.Pace) return;
            if (pace != 1f && o.Pace != 1f && Mathf.Abs(pace - o.Pace) < 0.02f) return;
            var a = o.Animator;
            if (a == null || a.WasCollected) { o.Pace = 1f; return; }
            try
            {
                if (o.Pace == 1f) o.SpeedBefore = a.speed;   // leaving the game's own speed: remember it
                a.speed = o.SpeedBefore * pace;
                o.Pace = pace;
            }
            catch (Exception e) { FruitBodies.ReportOnce("walk pace", e); o.Pace = 1f; }
        }

        /// <summary>Whether an order walks the 5.21 way (shaped paths, smoothed steering, corner pace, pivots).</summary>
        private static bool Smooth(Order o) => !o.Legacy && FruitLibConfig.NavSmoothWalking;

        private static Il2CppSystem.Object Holder => _holder ??= new Il2CppSystem.Object();

        private static bool Upright(FruitBody b) => b.Behaviour == FruitBehaviour.Standing || b.Behaviour == FruitBehaviour.Moving;

        /// <summary>Pelvis (or what is left) right now.</summary>
        private static Vector3 LiveCentre(FruitBody b)
        {
            var l = b.Limbs[FruitBodies.Pelvis];
            if (l.Present) return l.LivePosition;
            l = b.Limbs[FruitBodies.Torso];
            return l.Present ? l.LivePosition : b.Position;
        }

        private static readonly int[] Centres = { FruitBodies.Pelvis, FruitBodies.Torso, FruitBodies.Head };

        /// <summary>Flat distance from a body's pelvis to the nearest of another's pelvis, torso and head, right now.</summary>
        public static float Distance(FruitBody from, FruitBody to)
        {
            if (from == null || to == null) return float.PositiveInfinity;
            Vector3 me = LiveCentre(from);
            float best = float.PositiveInfinity;
            foreach (int i in Centres)
            {
                var l = to.Limbs[i];
                if (!l.Present) continue;
                Vector3 d = l.LivePosition - me;
                d.y = 0f;
                best = Mathf.Min(best, d.magnitude);
            }
            if (float.IsPositiveInfinity(best) && to.HasPosition) { Vector3 d = to.Position - me; d.y = 0f; best = d.magnitude; }
            return best;
        }

        /// <summary>From FruitLibMod.OnLateUpdate.</summary>
        internal static void LateTick()
        {
            TickHands();
            if (_swings.Count > 0) TickSwings();
            if (_orders.Count == 0) { PathWalkers = 0; return; }
            float now = Time.time, dt = Time.deltaTime;
            _done.Clear();
            _replans = 0;
            int walkers = 0;

            foreach (var kv in _orders)
            {
                var o = kv.Value;
                var b = o.Body;
                if (b.Creature == null || !b.Alive || !b.Conscious) { o.Result = FruitMoveResult.Lost; _done.Add(kv.Key); continue; }
                if (now >= o.Until) { o.Result = FruitMoveResult.TimedOut; _done.Add(kv.Key); continue; }
                if (!Bind(o)) continue;

                if (o.Raw)
                {
                    try
                    {
                        if (Upright(b) && !o.Requested)
                        {
                            if (!o.Walk.HoldsWalkRequest(Holder)) o.Walk.AddWalkRequest(Holder);
                            o.Requested = true;
                            o.StartAt = now;
                        }
                        if (o.Requested && StartRampSeconds > 0f && now - o.StartAt < StartRampSeconds + 0.1f)
                            o.Runtime.SetTargetMoveDirection(Vector3.forward * RampMagnitude(o, now));
                    }
                    catch (Exception e) { FruitBodies.ReportOnce("raw walk", e); _done.Add(kv.Key); }
                    continue;
                }

                try
                {
                    // Only on its feet: a body that is down stands up by itself first.
                    bool upright = Upright(b);
                    TrackCentre(o, dt);
                    o.PaceWanted = o.Run ? RunAnimationSpeed : 1f;
                    if (o.Target != null && !Steer(o, upright, now)) { _done.Add(kv.Key); continue; }
                    if (o.HasDest && FruitNav.Flat(o.Dest - LiveCentre(b)) <= o.StopDistance)
                    { o.Result = FruitMoveResult.Arrived; _done.Add(kv.Key); continue; }
                    if (o.HasDest && !Reslot(o, now)) { _done.Add(kv.Key); continue; }
                    if (IsGoto(o) && !o.Paused && !o.Turn)
                    {
                        Vector3 goal = o.Target == null ? o.Dest : o.Slotted ? o.Slot : LiveCentre(o.Target);
                        if (!FollowPath(o, goal, upright, now)) { _done.Add(kv.Key); continue; }
                        if (o.Path != null && o.Path.Found) walkers++;
                    }
                    else o.Pivot = false;
                    if (Telemetry) Measure(o, now, upright);
                    if (o.Paused)
                    {
                        Release(o);
                        if (upright) WatchTarget(o, dt);   // FruitBodyControl.Spots.cs
                        continue;
                    }
                    if (!upright) { SetPace(o, 1f); o.Pivot = false; continue; }
                    if (o.Turn)
                    {
                        if (!TickTurn(o, now, dt)) _done.Add(kv.Key);
                        continue;
                    }
                    if (o.TrafficBlocked)
                    {
                        Release(o);
                        o.Runtime.UpdateTargetYRot(SteerYaw(o, Mathf.Atan2(o.Dir.x, o.Dir.z) * Mathf.Rad2Deg, TurnRate, dt));
                        continue;
                    }
                    if (o.Pivot) { TickPivot(o, dt); continue; }
                    if (now < o.YieldUntil) { if (o.Requested) Release(o); continue; }   // letting someone through

                    if (!o.Requested)
                    {
                        if (!o.Walk.HoldsWalkRequest(Holder)) o.Walk.AddWalkRequest(Holder);
                        o.Requested = true;
                        o.StartAt = now;
                        ApplyFooting(o);
                    }
                    SetPace(o, o.PaceWanted);

                    var rt = o.Runtime;
                    float desired = Mathf.Atan2(o.Dir.x, o.Dir.z) * Mathf.Rad2Deg;
                    if (!_yawLogged)
                    {
                        _yawLogged = true;
                        float facing = Mathf.Atan2(b.Facing.x, b.Facing.z) * Mathf.Rad2Deg;
                        FruitLog.Info($"[FruitBodyControl] yaw check: CreatureYRotation={rt.CreatureYRotation:0.0} PuppeteerTargetYRot={rt.PuppeteerTargetYRot:0.0} facing={facing:0.0} (should match if degrees)");
                    }

                    float yaw = SteerYaw(o, desired, TurnRate * (o.Pace > 1.05f ? 1.4f : 1f), dt);
                    rt.UpdateTargetYRot(yaw);

                    // Move along the heading in the body's own frame: forward once it has turned,
                    // sideways or back while it is still turning. Walking smoothly, no more than
                    // MaxSideStep off its facing: the sideways clips are the wobbliest gait, so a
                    // body still turning walks an arc instead of crabbing.
                    Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, rt.CreatureYRotation, 0f)) * o.Dir;
                    local.y = 0f;
                    if (Smooth(o) && IsGoto(o))
                    {
                        float a = MathF.Atan2(local.x, local.z), cap = MaxSideStep * Mathf.Deg2Rad;
                        if (a > cap || a < -cap) { a = a > 0f ? cap : -cap; local = new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)); }
                    }
                    if (Smooth(o) && IsGoto(o))
                    {
                        // The gait caps sidestepping while yaw catches up. Check the heading it
                        // will actually walk too, so a safe desired turn cannot drive into a body.
                        Vector3 actual = Quaternion.Euler(0f, rt.CreatureYRotation, 0f) * local.normalized;
                        var skip = o.Target;
                        if (o.Slotted && skip != null && FruitNav.Flat(LiveCentre(skip) - LiveCentre(b)) < FruitNav.Flat(o.Slot - LiveCentre(b))) skip = null;
                        if (!TrafficClear(b, skip, LiveCentre(b), actual, Math.Max(o.Run ? 2.3f : 1.5f, FlatSpeed(o))))
                        { Release(o); continue; }
                    }
                    if (local.sqrMagnitude > 1e-4f) rt.SetTargetMoveDirection(local.normalized * RampMagnitude(o, now));
                }
                catch (Exception e)
                {
                    FruitBodies.ReportOnce("walk steering", e);
                    _done.Add(kv.Key);
                }
            }

            PathWalkers = walkers;
            foreach (var id in _done)
            {
                if (!_orders.TryGetValue(id, out var o)) continue;
                Release(o);
                _orders.Remove(id);
                if (!IsGoto(o)) continue;
                Summarise(o);
                // Before Ended: a handler that turns it some other way (to a spot's sofa) has the last word.
                if (o.Result == FruitMoveResult.Arrived)
                {
                    try { FaceGroup(o); }
                    catch (Exception e) { FruitBodies.ReportOnce("face the group", e); }
                }
                Ended(o.Body, o.Result);
            }
        }

        /// <summary>Re-aims an order at its target body. False when the order is finished: the target
        /// is gone, it arrived (approach), or it struck (attack).</summary>
        private static bool Steer(Order o, bool upright, float now)
        {
            var t = o.Target;
            if (t.Creature == null || FruitBodies.Get(t.Id) != t || !t.HasPosition) { o.Result = FruitMoveResult.Lost; return false; }

            Vector3 centre = LiveCentre(t), me = LiveCentre(o.Body);
            Vector3 to = centre - me;
            to.y = 0f;
            float d = to.magnitude;
            if (o.Strikes && upright && Distance(o.Body, t) <= Reach * 0.9f)
            {
                StartSwing(o.Body, t, o.Kind, o.Source, o.Strength);
                o.Result = FruitMoveResult.Arrived;
                return false;
            }
            if (UsesSlot(o)) return SteerToSlot(o, centre, me, d, now);   // FruitBodyControl.Spots.cs
            if (d <= o.StopDistance)
            {
                if (o.Strikes) { o.Paused = true; return true; }   // in range but down: strike once up
                if (!o.Keep) { o.Result = FruitMoveResult.Arrived; return false; }
                o.Paused = true;
                return true;
            }
            if (o.Paused && !o.Strikes && d <= o.StopDistance + 0.5f) return true;
            o.Paused = false;
            o.Dir = to / d;
            return true;
        }

        internal static void ResetForScene()
        {
            foreach (var o in _orders.Values) { try { Release(o); } catch { } }
            _orders.Clear();
            ResetStrikes();
            ResetHands();
            FruitPose.ResetForScene();
        }
    }
}
