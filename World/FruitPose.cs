using System;
using System.Collections.Generic;
using Il2CppLVA.Limbs.Systems;
using Il2CppLVA.Puppeteers.Variants.Humanoid;
using UnityEngine;

namespace FruitLib
{
    /// <summary>A bone of a body's rig, as the game numbers them (HPBoneType). Since 5.31.0.</summary>
    public enum FruitBone
    {
        Pelvis = 1,
        LeftThigh, LeftShin, LeftFoot,
        RightThigh, RightShin, RightFoot,
        LowerSpine, UpperSpine, Head,
        LeftUpperArm, LeftForearm, LeftHand,
        RightUpperArm, RightForearm, RightHand,
    }

    /// <summary>
    /// A pose: how far each bone turns from where the game's animation has it. Each turn is given
    /// in the body's own frame (x right, y up, z forward) as "move this bone's direction from
    /// <c>from</c> toward <c>to</c> by so many degrees", about the axis that was that body axis in
    /// the parent bone when the pose began. So a knee bends about the thigh's own hinge however
    /// the thigh is turned. Since 5.31.0.
    /// <code>
    /// var wave = new FruitPoseShape("wave")
    ///     .Bend(FruitBone.RightUpperArm, Vector3.down, Vector3.right, 80f)
    ///     .Bend(FruitBone.RightForearm, Vector3.down, Vector3.right, 90f);
    /// </code>
    /// </summary>
    public sealed class FruitPoseShape
    {
        internal struct Turn { public FruitBone Bone; public Vector3 Axis; public float Degrees; }
        internal readonly List<Turn> Turns = new List<Turn>();
        internal readonly List<FruitBone> Bones = new List<FruitBone>();

        public string Name { get; }
        /// <summary>Bends hips, knees or ankles: walking can't go on under it, and the rig's foot
        /// placement is held off while it is held.</summary>
        public bool UsesLegs { get; private set; }

        public FruitPoseShape(string name) { Name = string.IsNullOrEmpty(name) ? "pose" : name; }

        /// <summary>Turns a bone so its direction moves from <paramref name="from"/> toward
        /// <paramref name="to"/> (body frame) by <paramref name="degrees"/>. Several turns on one bone
        /// apply in order.</summary>
        public FruitPoseShape Bend(FruitBone bone, Vector3 from, Vector3 to, float degrees)
        {
            Vector3 axis = Vector3.Cross(from, to);
            if (axis.sqrMagnitude < 1e-6f) throw new ArgumentException($"{Name}: {bone} turns between parallel directions");
            Turns.Add(new Turn { Bone = bone, Axis = axis.normalized, Degrees = degrees });
            if (!Bones.Contains(bone)) Bones.Add(bone);
            if (bone >= FruitBone.LeftThigh && bone <= FruitBone.RightFoot) UsesLegs = true;
            return this;
        }

        /// <summary>The same turn on a right-side bone and, mirrored, on its left twin.</summary>
        public FruitPoseShape BendBoth(FruitBone rightBone, Vector3 from, Vector3 to, float degrees)
        {
            Bend(rightBone, from, to, degrees);
            return Bend(LeftOf(rightBone), Mirror(from), Mirror(to), degrees);
        }

        private static Vector3 Mirror(Vector3 v) => new Vector3(-v.x, v.y, v.z);

        private static FruitBone LeftOf(FruitBone b)
        {
            switch (b)
            {
                case FruitBone.RightThigh:    return FruitBone.LeftThigh;
                case FruitBone.RightShin:     return FruitBone.LeftShin;
                case FruitBone.RightFoot:     return FruitBone.LeftFoot;
                case FruitBone.RightUpperArm: return FruitBone.LeftUpperArm;
                case FruitBone.RightForearm:  return FruitBone.LeftForearm;
                case FruitBone.RightHand:     return FruitBone.LeftHand;
                default: throw new ArgumentException($"{b} is not a right-side bone");
            }
        }

        public override string ToString() => Name;

        // ── Built in ──
        // Angles from the body's rest: the thigh moves forward from hanging down, the shin back from
        // the thigh, the foot is turned back to flat (it carries the thigh's and shin's turns).

        /// <summary>No bones: only takes the stance over (the body is held up and won't be judged
        /// fallen over a tilt), e.g. while carrying something awkward.</summary>
        public static readonly FruitPoseShape Steady = new FruitPoseShape("steady");

        /// <summary>A deep squat, back leaning in, low enough to reach the floor in front.</summary>
        public static readonly FruitPoseShape Crouch = new FruitPoseShape("crouch")
            .BendBoth(FruitBone.RightThigh, Vector3.down, Vector3.forward, 95f)
            .BendBoth(FruitBone.RightShin,  Vector3.down, Vector3.back, 135f)
            .BendBoth(FruitBone.RightFoot,  Vector3.down, Vector3.forward, 40f)
            .Bend(FruitBone.LowerSpine, Vector3.up, Vector3.forward, 25f)
            .Bend(FruitBone.UpperSpine, Vector3.up, Vector3.forward, 20f)
            .Bend(FruitBone.Head, Vector3.forward, Vector3.down, 10f);

        /// <summary>Down on the right knee, left foot planted.</summary>
        public static readonly FruitPoseShape Kneel = new FruitPoseShape("kneel")
            .Bend(FruitBone.LeftThigh,  Vector3.down, Vector3.forward, 90f)
            .Bend(FruitBone.LeftShin,   Vector3.down, Vector3.back, 90f)
            .Bend(FruitBone.RightThigh, Vector3.down, Vector3.forward, 10f)
            .Bend(FruitBone.RightShin,  Vector3.down, Vector3.back, 100f)
            .Bend(FruitBone.RightFoot,  Vector3.down, Vector3.forward, 20f)
            .Bend(FruitBone.LowerSpine, Vector3.up, Vector3.forward, 10f);

        /// <summary>Both hands raised beside the head.</summary>
        public static readonly FruitPoseShape HandsUp = new FruitPoseShape("hands up")
            .BendBoth(FruitBone.RightUpperArm, Vector3.down, Vector3.right, 85f)
            .BendBoth(FruitBone.RightForearm,  Vector3.down, Vector3.right, 95f);
    }

    /// <summary>
    /// Holds a body in a pose, apart from what the game thinks of its stance. Since 5.31.0.
    ///
    /// How the game stands a body up (Release, Ghidra group humanoid-stance, 2026-10-11): one
    /// number does two jobs. The puppeteer's Balance (MoveStateDynamicBalanceControl, every
    /// FixedUpdate of the move state) rises toward 100 while a ray along the pelvis's own down
    /// axis finds the floor, falls toward 0 at 12/s when it doesn't for a moment, and is capped
    /// by the pelvis tilt (62.5 at 26 degrees or more). That same Balance is the strength of the
    /// pelvis's world-anchored rotation drive (PelvisMuscleForce), the hand that holds a body
    /// upright. GeneralMuscleForce, which feeds every limb's joint drive, is pulled down the same
    /// way while "not grounded". And HumanoidStandabilityChecker drops the body into the fall
    /// state once the pelvis leans past ~37 degrees. A body that bends to reach the floor tilts
    /// its pelvis, its ray misses, Balance and muscle sag, the hold lets go, it folds.
    ///
    /// A held pose splits that into three things set apart:
    ///   - the pose: per-bone targets through the game's own override layer
    ///     (HumanoidReprojectorOverrideLayer, the one the agony writhe uses), applied after the
    ///     animation is copied onto the rig the muscles follow and before its IK;
    ///   - tension: a multiple on every limb's joint drive (the game's muscle value times it, so a
    ///     wounded or bloodless limb stays weak);
    ///   - support: how hard the pelvis is held, written where the puppeteer's Balance goes (so a
    ///     lost leg still costs what it costs further down).
    /// While a pose is held its body is not judged fallen over its tilt (only over being knocked
    /// fast), and its stance inputs are ours. Leaving the move state (agony, a real fall) drops
    /// the pose at once, before the game's own overrides want those bones.
    /// </summary>
    public static class FruitPose
    {
        /// <summary>Seconds to blend into and out of a pose when none is given.</summary>
        public static float DefaultBlend = 0.45f;

        /// <summary>Pelvis speed squared (m²/s²) that counts as knocked off its feet: the pose is
        /// dropped and the game takes over. The game's own border is 95.</summary>
        public static float KnockedVelocitySqr = 60f;

        /// <summary>Pelvis upright (dot with up) below which a posed body has fallen over after all,
        /// held for <see cref="FallenSeconds"/>.</summary>
        public static float FallenUpDot = 0.35f, FallenSeconds = 0.4f;

        /// <summary>Fastest a bone turns into or out of a pose, degrees per second: a blend asked
        /// for quicker than this takes longer (a body thrown down fast carries on past its pose).
        /// Since 5.31.1.</summary>
        public static float DegreesPerSecond = 160f;

        /// <summary>Joint damping while posed, as a multiple of the game's full-strength damping:
        /// limbs settle instead of swinging past their targets. Since 5.31.1.</summary>
        public static float Damping = 2f;

        /// <summary>
        /// Keeps a posed body's centre of mass over what it stands or kneels on: a pull on the
        /// limbs above the support, toward it, against the drift and its speed (the game holds the
        /// pelvis's rotation, nothing holds its place). Strongest pull <see cref="AssistMax"/>
        /// (m/s² at the centre of mass) times the support; none within <see cref="AssistSlack"/>
        /// metres of the support's middle. Runs on for up to <see cref="SettleSeconds"/> after a
        /// release, while the body rises. Since 5.31.1.
        /// </summary>
        public static bool BalanceAssist = true;
        public static float AssistMax = 3.5f, AssistSlack = 0.04f, AssistSpring = 30f, AssistDamper = 11f, SettleSeconds = 0.8f;

        /// <summary>Raised when a pose ends: "released" when asked and blended out, otherwise why
        /// it was dropped (knocked, fell, left its feet, gone).</summary>
        public static event Action<FruitBody, FruitPoseShape, string> Ended;

        private sealed class Slot
        {
            public FruitBone Bone;
            public HumanoidBoneOverride Ov;
            /// <summary>The animation's local rotation when the bone was taken: every target is turned from it.</summary>
            public Quaternion Base;
            public Quaternion From, To, Cur;
            public float FromShare, ToShare, Share;
        }

        private sealed class Held
        {
            public FruitBody Body;
            public IntPtr Creature;
            public FruitPoseShape Shape;
            public string Source;
            public HumanoidReprojectorOverrideLayer Layer;
            public readonly List<Slot> Slots = new List<Slot>();
            public float BlendStart, BlendTime;
            public bool Releasing;
            public float Tension = 1f, Support = 1f, AppliedTension = 1f, Weight;
            public float FallenSince = -1f;
            /// <summary>Blended out: the stance stays ours while the body rises, until this time.</summary>
            public float SettleUntil = -1f;
            public bool Damped;
            public readonly Rigidbody[] Rbs = new Rigidbody[FruitBodies.LimbCount];
            /// <summary>Last pull, m/s² at the centre of mass, for the trace.</summary>
            public Vector3 Assist, ComOffset;
            public float ComSpeed;
            // Native hooks find a held body by these.
            public IntPtr Runtime, Tools;
            public readonly List<DefaultMuscleForce> Muscles = new List<DefaultMuscleForce>();
            /// <summary>Each limb's joint, by its DefaultMuscleForce pointer.</summary>
            public readonly Dictionary<IntPtr, ConfigurableJoint> Joints = new Dictionary<IntPtr, ConfigurableJoint>();
            public float ResyncAt;
            public Il2CppData.CustomTypes.LimitedValue.Dependecies.BaseChannelWriter BalanceCh, MuscleCh;
            public float BalanceMax = 100f, MuscleMax = 100f;
        }

        private static readonly Dictionary<int, Held> _held = new Dictionary<int, Held>();
        private static readonly Dictionary<IntPtr, Held> _byRuntime = new Dictionary<IntPtr, Held>();
        private static readonly Dictionary<IntPtr, Held> _byTools = new Dictionary<IntPtr, Held>();
        private static readonly Dictionary<IntPtr, Held> _byMuscle = new Dictionary<IntPtr, Held>();
        private static readonly List<Held> _tick = new List<Held>();

        // ── Asking ──

        /// <summary>The pose a body holds (or is blending out of), or null.</summary>
        public static FruitPoseShape Current(FruitBody body)
            => body != null && _held.TryGetValue(body.Id, out var h) ? h.Shape : null;

        /// <summary>Whether the body's stance is ours right now (a pose held or blending out).</summary>
        public static bool IsHolding(FruitBody body) => body != null && _held.ContainsKey(body.Id);

        /// <summary>How far into its pose a body is, 0..1.</summary>
        public static float Weight(FruitBody body)
            => body != null && _held.TryGetValue(body.Id, out var h) ? h.Weight : 0f;

        /// <summary>For probes: the balance pull last applied (m/s²) and the centre of mass's flat
        /// offset from the support's middle (m). Since 5.31.1.</summary>
        public static bool AssistState(FruitBody body, out Vector3 pull, out Vector3 offset)
        {
            pull = offset = Vector3.zero;
            if (body == null || !_held.TryGetValue(body.Id, out var h)) return false;
            pull = h.Assist; offset = h.ComOffset;
            return true;
        }

        /// <summary>For probes: each posed bone the rig's IK has turned away from the pose by more
        /// than <paramref name="degrees"/>, as "LowerSpine 31". Read after the pipeline ran. Since 5.31.1.</summary>
        public static string RigDrift(FruitBody body, float degrees = 8f)
        {
            if (body == null || !_held.TryGetValue(body.Id, out var h)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var s in h.Slots)
            {
                if (s.Share < 0.95f) continue;
                try
                {
                    float off = Quaternion.Angle(s.Ov.Bone.localRotation, s.Cur);
                    if (off > degrees) sb.Append(sb.Length > 0 ? ", " : "").Append(s.Bone).Append(' ').Append(Mathf.RoundToInt(off));
                }
                catch { }
            }
            return sb.ToString();
        }

        // ── Holding ──

        /// <summary>
        /// Blends a body into a pose and holds it there until <see cref="Release"/>. Asking for
        /// another pose while one is held blends from the one to the other. Walking orders stop
        /// first if the pose uses the legs. False if the body isn't standing in the game's move
        /// state (down, getting up, in agony) or its rig can't be reached.
        /// </summary>
        /// <param name="tension">Multiple on every limb's muscle (1 = the game's own).</param>
        /// <param name="support">How hard the pelvis is held upright, 0..1 (1 = as if perfectly balanced).</param>
        public static bool Hold(FruitBody body, FruitPoseShape shape, float blendSeconds = -1f,
                                float tension = 1f, float support = 1f, string source = "FruitLib")
        {
            if (body == null || shape == null || !body.Alive || !body.Conscious) return false;
            if (!InMoveState(body)) return false;
            if (blendSeconds < 0f) blendSeconds = DefaultBlend;

            if (!_held.TryGetValue(body.Id, out var h) || h.Creature != body.CreaturePtr)
            {
                if (h != null) Drop(h, "rebound");
                h = Bind(body);
                if (h == null) return false;
            }
            if (shape.UsesLegs && FruitBodyControl.IsWalking(body)) FruitBodyControl.Stop(body);

            h.Shape = shape;
            h.Source = string.IsNullOrEmpty(source) ? "FruitLib" : source;
            h.Tension = Mathf.Clamp(tension, 0f, 2f);
            h.Support = Mathf.Clamp01(support);
            h.Releasing = false;
            h.FallenSince = -1f;
            h.SettleUntil = -1f;
            Retarget(h, shape, blendSeconds);
            return true;
        }

        /// <summary>Blends out of the pose; the stance goes back to the game once it is out.</summary>
        public static void Release(FruitBody body, float blendSeconds = -1f)
        {
            if (body == null || !_held.TryGetValue(body.Id, out var h) || h.Releasing) return;
            h.Releasing = true;
            Retarget(h, null, blendSeconds < 0f ? DefaultBlend : blendSeconds);
        }

        /// <summary>Changes the muscle multiple of a held pose.</summary>
        public static void SetTension(FruitBody body, float tension)
        {
            if (body != null && _held.TryGetValue(body.Id, out var h)) h.Tension = Mathf.Clamp(tension, 0f, 2f);
        }

        /// <summary>Changes how hard a held pose's pelvis is held, 0..1.</summary>
        public static void SetSupport(FruitBody body, float support)
        {
            if (body != null && _held.TryGetValue(body.Id, out var h)) h.Support = Mathf.Clamp01(support);
        }

        /// <summary>A walk is starting: a pose on the legs gives way quickly.</summary>
        internal static void YieldToWalk(FruitBody body)
        {
            if (body != null && _held.TryGetValue(body.Id, out var h) && !h.Releasing && h.Shape.UsesLegs) Release(body, 0.25f);
        }

        // ── Binding ──

        private static bool InMoveState(FruitBody b)
        {
            try { return b.Bind.Ai != null && FruitBodies.TypeNameOf(b.Bind.Ai.ActiveState) == "HumanoidMoveState"; }
            catch { return false; }
        }

        private static Held Bind(FruitBody b)
        {
            var h = new Held { Body = b, Creature = b.CreaturePtr };
            try
            {
                var refs = b.Bind.Refs;
                h.Layer = refs?.Animation?.Pipeline?.OverrideLayer;
                if (h.Layer == null) { FruitLog.Info($"[FruitPose] #{b.Id} has no rig override layer."); return null; }
                h.Runtime = refs.RuntimeData?.Pointer ?? IntPtr.Zero;
                try { h.Tools = refs.IK?.GrounderTools?.Pointer ?? IntPtr.Zero; } catch { }
                var p = refs.Parameters;
                if (p != null)
                {
                    h.BalanceCh = p.BalanceChannel;
                    h.MuscleCh = p.GeneralMuscleForceChannel;
                    if (h.BalanceCh != null) h.BalanceMax = h.BalanceCh.MaxValue;
                    if (h.MuscleCh != null) h.MuscleMax = h.MuscleCh.MaxValue;
                }
            }
            catch (Exception e) { FruitBodies.ReportOnce("pose binding", e); return null; }

            // Each limb's muscle sets its joint drive; the hook after it scales the spring by the
            // tension. Damping is set through the muscle's own override (used as is by the game).
            for (int i = 0; i < FruitBodies.LimbCount; i++)
            {
                var limb = b.Limbs[i];
                try { h.Rbs[i] = limb.Present && limb.Physics != null && !limb.Physics.WasCollected ? limb.Physics.m_rb : null; }
                catch { h.Rbs[i] = null; }
                if (i == FruitBodies.Pelvis || !limb.Present || limb.Native == null) continue;
                try
                {
                    var iface = limb.Native.EntityInternalSystemsPublic?.GetSystemExternalInteraction<IDefaultMuscleForceInteraction>(false);
                    if (iface == null) continue;
                    var muscle = new DefaultMuscleForce(iface.Pointer);
                    var setter = muscle.m_muscleSlerpDriveSetter;
                    if (setter == null) continue;
                    var joint = setter.m_jointProvider?.m_joint;
                    if (joint == null) continue;
                    h.Muscles.Add(muscle);
                    h.Joints[muscle.Pointer] = joint;
                    if (Damping > 0f && Math.Abs(Damping - 1f) > 0.01f)
                    {
                        muscle.OverrideMinPositionDamper(setter.m_originalJointPositionDamper * Damping);
                        h.Damped = true;
                    }
                }
                catch (Exception e) { FruitBodies.ReportOnce("pose limb muscle", e); }
            }

            _held[b.Id] = h;
            if (h.Runtime != IntPtr.Zero) _byRuntime[h.Runtime] = h;
            if (h.Tools != IntPtr.Zero) _byTools[h.Tools] = h;
            foreach (var m in h.Muscles) _byMuscle[m.Pointer] = h;
            return h;
        }

        // ── Blending ──

        /// <summary>Points every bone at the new shape (null: back to the animation) from wherever it is now.</summary>
        private static void Retarget(Held h, FruitPoseShape shape, float blend)
        {
            var b = h.Body;
            Quaternion body = BodyFrame(b);
            h.BlendStart = Time.fixedTime;
            h.BlendTime = Mathf.Max(0.01f, blend);

            // Bones the shape needs that aren't overridden yet.
            if (shape != null)
                foreach (var bone in shape.Bones)
                    if (FindSlot(h, bone) == null)
                    {
                        try
                        {
                            var ov = h.Layer.StartOverride((HPBoneType)(int)bone);
                            if (ov == null || ov.Bone == null) continue;
                            var q = ov.Bone.localRotation;
                            h.Slots.Add(new Slot { Bone = bone, Ov = ov, Base = q, From = q, To = q, Cur = q });
                        }
                        catch (Exception e) { FruitBodies.ReportOnce($"pose override {bone}", e); }
                    }

            foreach (var s in h.Slots)
            {
                s.From = s.Cur;
                s.FromShare = s.Share;
                if (shape == null || !shape.Bones.Contains(s.Bone)) { s.To = s.Cur; s.ToShare = 0f; continue; }
                try
                {
                    // From the animation's local rotation, each turn about the body axis as the
                    // parent bone carries it (a parent already posed carries it posed: right for
                    // the hinge-like bends here, which turn about that same axis).
                    var parent = s.Ov.Bone.parent;
                    Quaternion local = s.Base;
                    Quaternion parentWorld = parent != null ? parent.rotation : Quaternion.identity;
                    Quaternion inv = Quaternion.Inverse(parentWorld);
                    foreach (var t in shape.Turns)
                    {
                        if (t.Bone != s.Bone) continue;
                        Vector3 axis = inv * (body * t.Axis);
                        local = Quaternion.AngleAxis(t.Degrees, axis) * local;
                    }
                    s.To = local;
                    s.ToShare = 1f;
                }
                catch (Exception e) { FruitBodies.ReportOnce($"pose target {s.Bone}", e); s.To = s.Cur; s.ToShare = 0f; }
            }

            // No bone turns faster than DegreesPerSecond.
            float most = 0f;
            foreach (var s in h.Slots)
            {
                float turn = Quaternion.Angle(s.Cur, s.To) * Mathf.Max(s.Share, s.ToShare)
                           + Quaternion.Angle(s.Base, s.Cur) * Mathf.Abs(s.ToShare - s.Share);
                if (turn > most) most = turn;
            }
            if (DegreesPerSecond > 0f) h.BlendTime = Mathf.Max(h.BlendTime, most / DegreesPerSecond);
        }

        private static Slot FindSlot(Held h, FruitBone bone)
        {
            foreach (var s in h.Slots) if (s.Bone == bone) return s;
            return null;
        }

        private static Quaternion BodyFrame(FruitBody b)
        {
            Vector3 fwd = b.Facing; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            return Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }

        /// <summary>From FruitLibMod.OnFixedUpdate.</summary>
        internal static void FixedTick()
        {
            if (_held.Count == 0) return;
            _tick.Clear();
            _tick.AddRange(_held.Values);
            float now = Time.fixedTime;
            foreach (var h in _tick)
            {
                try { Step(h, now); }
                catch (Exception e) { FruitBodies.ReportOnce("pose step", e); Drop(h, "failed"); }
            }
        }

        private static void Step(Held h, float now)
        {
            var b = h.Body;
            if (b.Creature == null || FruitBodies.Get(b.Id) != b || b.CreaturePtr != h.Creature) { Drop(h, "gone"); return; }
            if (!b.Alive || !b.Conscious) { Drop(h, "went limp"); return; }
            if (!InMoveState(b)) { Drop(h, "left its feet"); return; }

            float k = Mathf.Clamp01((now - h.BlendStart) / h.BlendTime);
            k = k * k * (3f - 2f * k);
            float weight = 0f;
            bool any = false;
            for (int i = h.Slots.Count - 1; i >= 0; i--)
            {
                var s = h.Slots[i];
                s.Cur = Quaternion.Slerp(s.From, s.To, k);
                s.Share = Mathf.Lerp(s.FromShare, s.ToShare, k);
                if (s.Ov == null || s.Ov.WasCollected) { h.Slots.RemoveAt(i); continue; }
                if (k >= 1f && s.ToShare <= 0f)
                {
                    try { h.Layer.StopOverride(s.Ov); } catch (Exception e) { FruitBodies.ReportOnce("pose stop override", e); }
                    h.Slots.RemoveAt(i);
                    continue;
                }
                s.Ov.Pull(s.Cur, s.Share);
                weight = Mathf.Max(weight, s.Share);
                any = true;
            }
            h.Weight = h.Releasing ? weight : (h.Shape.Bones.Count == 0 ? 1f : weight);

            float tension = Mathf.Lerp(1f, h.Tension, h.Releasing ? weight : Mathf.Max(weight, k));
            if (Mathf.Abs(tension - h.AppliedTension) > 0.02f || (Mathf.Abs(h.AppliedTension - 1f) > 0.02f && now >= h.ResyncAt))
            {
                // Also every half second while scaled: some of the game's own resyncs may not pass the hook.
                h.AppliedTension = tension;
                h.ResyncAt = now + 0.5f;
                Resync(h);
            }

            // Blended out: the body rises under the game's animation, still held and steadied,
            // until it is still over its feet or SettleSeconds have passed.
            float settle = 1f;
            if (h.Releasing && k >= 1f && !any)
            {
                if (h.SettleUntil < 0f) h.SettleUntil = now + SettleSeconds;
                float left = h.SettleUntil - now;
                bool still = h.ComOffset.magnitude <= AssistSlack * 2f && h.ComSpeed < 0.15f && left < SettleSeconds - 0.25f;
                if (left <= 0f || still) { Finish(h, "released"); return; }
                settle = Mathf.Clamp01(left / Mathf.Max(0.01f, SettleSeconds));
            }
            Assist(h, settle);
        }

        // ── Balance ──

        // Limbs the balance pull acts on: everything above the knees. Feet and shins hold the ground.
        private static readonly int[] Upper = { 0, 1, 4, 7, 8, 9, 10, 11, 12, 13, 14 };
        private const int LeftShin = 2, LeftFoot = 3, RightShin = 5, RightFoot = 6;

        /// <summary>Pulls the centre of mass back over what the body stands or kneels on, against
        /// its drift and speed (flat only).</summary>
        private static void Assist(Held h, float strength)
        {
            h.Assist = Vector3.zero;
            if (!BalanceAssist || strength <= 0f || h.Support <= 0f || FruitBodyControl.IsWalking(h.Body)) return;

            float total = 0f, upper = 0f;
            Vector3 com = Vector3.zero, vel = Vector3.zero;
            for (int i = 0; i < h.Rbs.Length; i++)
            {
                var rb = h.Rbs[i];
                if (rb == null || rb.WasCollected) { h.Rbs[i] = null; continue; }
                float m = rb.mass;
                total += m;
                com += rb.position * m;
                vel += rb.linearVelocity * m;
            }
            foreach (int i in Upper) if (h.Rbs[i] != null) upper += h.Rbs[i].mass;
            if (total <= 0f || upper <= 0f) return;
            com /= total; vel /= total;

            // The support: both feet, and a shin lying at their level (a knee down).
            var lf = h.Rbs[LeftFoot]; var rf = h.Rbs[RightFoot];
            if (lf == null && rf == null) return;
            float floor = Mathf.Min(lf != null ? lf.position.y : float.MaxValue, rf != null ? rf.position.y : float.MaxValue);
            Vector3 sum = Vector3.zero; int n = 0;
            if (lf != null) { sum += lf.position; n++; }
            if (rf != null) { sum += rf.position; n++; }
            var ls = h.Rbs[LeftShin]; var rs = h.Rbs[RightShin];
            if (ls != null && ls.position.y < floor + 0.15f) { sum += ls.position; n++; }
            if (rs != null && rs.position.y < floor + 0.15f) { sum += rs.position; n++; }
            Vector3 centre = sum / n;

            Vector3 off = com - centre; off.y = 0f;
            vel.y = 0f;
            h.ComOffset = off;
            h.ComSpeed = vel.magnitude;
            float d = off.magnitude;
            Vector3 spring = d > AssistSlack ? off * ((d - AssistSlack) / d) : Vector3.zero;
            Vector3 a = -(spring * AssistSpring + vel * AssistDamper);
            float cap = AssistMax * h.Support * strength;
            if (a.sqrMagnitude > cap * cap) a = a.normalized * cap;
            h.Assist = a;
            if (a.sqrMagnitude < 1e-4f) return;

            // On the upper body only, enough that the whole body's centre gets a.
            Vector3 push = a * (total / upper);
            foreach (int i in Upper)
            {
                var rb = h.Rbs[i];
                if (rb != null) rb.AddForce(push, ForceMode.Acceleration);
            }
        }

        /// <summary>Has every limb set its joint drive again (through the hook, at the current tension).</summary>
        private static void Resync(Held h)
        {
            foreach (var m in h.Muscles)
            {
                try { if (m != null && !m.WasCollected) m.SyncJointDriveWithMuscleForceValue(); }
                catch (Exception e) { FruitBodies.ReportOnce("pose muscle sync", e); }
            }
        }

        // ── Ending ──

        private static void Drop(Held h, string why)
        {
            foreach (var s in h.Slots)
            {
                try { if (s.Ov != null && !s.Ov.WasCollected && h.Layer != null && !h.Layer.WasCollected) h.Layer.StopOverride(s.Ov); }
                catch (Exception e) { FruitBodies.ReportOnce("pose drop override", e); }
            }
            h.Slots.Clear();
            Finish(h, why);
        }

        private static void Finish(Held h, string why)
        {
            if (_held.TryGetValue(h.Body.Id, out var cur) && cur == h) _held.Remove(h.Body.Id);
            if (h.Runtime != IntPtr.Zero && _byRuntime.TryGetValue(h.Runtime, out var r) && r == h) _byRuntime.Remove(h.Runtime);
            if (h.Tools != IntPtr.Zero && _byTools.TryGetValue(h.Tools, out var t) && t == h) _byTools.Remove(h.Tools);
            foreach (var m in h.Muscles) if (m != null && _byMuscle.TryGetValue(m.Pointer, out var x) && x == h) _byMuscle.Remove(m.Pointer);
            bool resync = Math.Abs(h.AppliedTension - 1f) > 0.001f;
            h.AppliedTension = 1f;
            if (h.Damped)
            {
                h.Damped = false;
                resync = true;
                foreach (var m in h.Muscles)
                {
                    try { if (m != null && !m.WasCollected) m.ClearMinPositionDamperOverride(); }
                    catch (Exception e) { FruitBodies.ReportOnce("pose damping clear", e); }
                }
            }
            if (resync) Resync(h);
            if (Ended == null) return;
            try { Ended(h.Body, h.Shape, why); }
            catch (Exception e) { FruitBodies.ReportOnce("FruitPose.Ended handler", e); }
        }

        internal static void ResetForScene()
        {
            _held.Clear();
            _byRuntime.Clear();
            _byTools.Clear();
            _byMuscle.Clear();
        }

        // ── What the native hooks ask (FruitPose.Native.cs) ──

        internal static bool AnyHeld => _held.Count > 0;

        /// <summary>The puppeteer's balance step for a held body: our support and full muscle instead.</summary>
        internal static bool OwnBalance(IntPtr runtime)
        {
            if (!_byRuntime.TryGetValue(runtime, out var h)) return false;
            try
            {
                h.BalanceCh?.Set(h.Support * h.BalanceMax);
                h.MuscleCh?.Set(h.MuscleMax);
            }
            catch (Exception e) { FruitBodies.ReportOnce("pose balance", e); }
            return true;
        }

        /// <summary>The game asks whether a held body can still stand: yes, unless it was knocked
        /// fast or has lain over for a moment. Null: not ours, ask the game.</summary>
        internal static bool? OwnStandability(IntPtr runtime, float pelvisVelocitySqr, float pelvisUpDot)
        {
            if (!_byRuntime.TryGetValue(runtime, out var h)) return null;
            if (pelvisVelocitySqr >= KnockedVelocitySqr) { Drop(h, "knocked"); return null; }
            float now = Time.fixedTime;
            if (pelvisUpDot < FallenUpDot)
            {
                if (h.FallenSince < 0f) h.FallenSince = now;
                else if (now - h.FallenSince >= FallenSeconds) { Drop(h, "fell"); return null; }
            }
            else h.FallenSince = -1f;
            return true;
        }

        /// <summary>The move state is being left: let go of the bones now, before agony wants them.</summary>
        internal static void OnMoveStateExit(IntPtr runtime)
        {
            if (_byRuntime.TryGetValue(runtime, out var h)) Drop(h, "left its feet");
        }

        /// <summary>The grounder's weight for a held body: held off as far as the legs are posed.</summary>
        internal static float GrounderWeight(IntPtr tools, float weight)
        {
            if (!_byTools.TryGetValue(tools, out var h)) return weight;
            float legs = 0f;
            foreach (var s in h.Slots)
                if (s.Bone >= FruitBone.LeftThigh && s.Bone <= FruitBone.RightFoot) legs = Mathf.Max(legs, s.Share);
            return weight * (1f - legs);
        }

        /// <summary>A limb's joint drive strength for a held body: the game's value times the tension.</summary>
        internal static bool AnyMuscleScaled => _byMuscle.Count > 0;

        /// <summary>A limb's muscle has just set its joint drive: scale the spring by the held pose's
        /// tension. (Hooking the setter itself is not possible: its Nullable&lt;float&gt; argument
        /// comes in a register, and Il2CppInterop's trampoline reads it as a pointer.)</summary>
        internal static void AfterMuscleSync(IntPtr muscle)
        {
            if (!_byMuscle.TryGetValue(muscle, out var h) || Mathf.Abs(h.AppliedTension - 1f) < 0.001f) return;
            if (!h.Joints.TryGetValue(muscle, out var joint) || joint == null || joint.WasCollected) return;
            var d = joint.slerpDrive;
            d.positionSpring *= h.AppliedTension;
            joint.slerpDrive = d;
        }
        internal static bool AnyGrounderHeld => _byTools.Count > 0;
    }
}
