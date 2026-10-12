using System;
using System.Collections.Generic;
using Il2CppLVA.NodesHierarchy.Benchmark.Variants;
using Il2CppLVA.Puppeteers.Variants.Humanoid;
using UnityEngine;

namespace FruitLib
{
    // Hitting. The game has no melee, so a blow is put together from three of its own parts:
    //
    // The attacker's hands go through the hand IK the game's wound covering uses
    // (IKReferences.Left/RightHandEffectorPlacer: SetLocalPosition flies the hand's FBBIK effector
    // along a curve to a point in a parent's space, RemovePosition eases it back). The physical
    // body copies the IK rig's pose, so the arm really reaches out. Targets are given in the rig
    // pelvis's space, offset from the physical pelvis: placed against the physical body, the rig
    // reaches across any gap between the two with straight arms (BombsAway's flashbang found
    // that).
    //
    // The victim flinches through its own FinalIK hit reaction: the same HitReactionProvider
    // hit points a pain spike drives (PuppeteerPainPerception.OnPainSpike, Ghidra 2026-10-10),
    // looked up by limb (HumanoidNodeTagValue) and hit with a force along the blow.
    //
    // The push is an impulse scaled by the victim's real mass (the sum of its limb rigidbodies):
    // the 5.14.0 fixed impulses (16 N·s, 90 N·s) were absorbed by the balance without a trace.
    //
    // Punch: the hand chambers by the shoulder (WindUp), then darts through the struck point.
    // Shove: both hands go to the chest. The blow lands when the hand gets there (or after
    // MaxExtendTime), then the hands are held a moment and let go.
    public static partial class FruitBodyControl
    {
        /// <summary>How close (flat metres, attacker's pelvis to the victim's pelvis, torso or head)
        /// a body must be to land a blow. Since 5.14.0.</summary>
        public static float Reach = 1.1f;

        /// <summary>Seconds a punch is chambered by the shoulder before it goes. Since 5.14.0.</summary>
        public static float WindUp = 0.15f;

        /// <summary>Longest a hand may take to reach the target before the blow lands anyway, seconds. Since 5.15.0.</summary>
        public static float MaxExtendTime = 0.35f;

        /// <summary>Hand IK flight speed for a punch and a shove (the game's wound covering flies at
        /// 1). Since 5.15.0.</summary>
        public static float PunchHandSpeed = 5f, ShoveHandSpeed = 3f;

        /// <summary>
        /// How hard a blow pushes, as the speed it would give the victim's whole mass, m/s. A punch
        /// goes 70 % into the struck limb and 30 % into the torso; a shove 55 % into the torso and
        /// 45 % into the pelvis. Since 5.15.0 (replacing 5.14.0's fixed PunchImpulse/ShoveImpulse).
        /// </summary>
        public static float PunchSpeed = 0.6f, ShoveSpeed = 1.8f;

        /// <summary>Strength of the victim's flinch (the game's FinalIK hit reaction force) for a punch
        /// and a shove. Since 5.15.0.</summary>
        public static float PunchReaction = 1f, ShoveReaction = 1.5f;

        /// <summary>Per-voxel damage signal a punch leaves on the struck limb's surface (a bruise; a
        /// voxel holds 100, so one punch never removes one, repeated punches on one spot can).
        /// 0 = no bruise. Since 5.14.0.</summary>
        public static float PunchBruise = 35f;

        private enum Stage { Chamber, Extend, Hold }

        private sealed class Swing
        {
            public FruitBody   Attacker, Victim;
            public FruitStrike Kind;
            public string      Source;
            public float       Strength;
            public Stage       Stage;
            public float       StageAt;
            public int         Limb;
            public bool        Landed;
            // Hands: Main punches (or shoves with the other); Off is a shove's second hand.
            public HandEffectorPlacer Main, Off;
            public int         MainHand = 14, OffHand = 11;
            public bool        MainPlaced, OffPlaced;
            public Vector3     Contact;
        }

        private static readonly List<Swing> _swings = new List<Swing>();
        private static readonly System.Random _rng = new System.Random();

        private const int LeftHand = 11, RightHand = 14, LeftForearm = 10, RightForearm = 13;

        // ── Public ──

        /// <summary>
        /// Hits another body now, if it is within <see cref="Reach"/> and the attacker is conscious
        /// and on its feet. Returns false if it can't (out of reach, down, already swinging). The
        /// blow lands when the hand gets there and is published from <paramref name="source"/> as
        /// an "Attacked" event, or "SwungAt" if the victim got out of reach meanwhile. Since 5.14.0.
        /// </summary>
        public static bool Strike(FruitBody attacker, FruitBody victim, FruitStrike kind = FruitStrike.Punch,
                                  string source = "FruitLib", float strength = 1f)
        {
            if (attacker == null || victim == null || attacker == victim) return false;
            if (!attacker.Alive || !attacker.Conscious || !Upright(attacker) || !victim.HasPosition) return false;
            if (Swinging(attacker) || Distance(attacker, victim) > Reach) return false;
            StartSwing(attacker, victim, kind, string.IsNullOrEmpty(source) ? "FruitLib" : source, Mathf.Max(0f, strength));
            return true;
        }

        /// <summary>
        /// Puts one of a body's hands at a world point through the game's own hand IK (the one its
        /// wound covering uses), flying there at <paramref name="speed"/> (the covering flies at 1).
        /// It stays until <see cref="ReleaseHand"/>, or until the game's covering takes the hand for
        /// a wound. Since 5.15.0.
        /// </summary>
        public static bool PlaceHand(FruitBody body, bool left, Vector3 worldPoint, float speed = 1f)
        {
            if (body == null || !body.Alive) return false;
            try { return Place(body, Placer(body, left), worldPoint, speed); }
            catch (Exception e) { FruitBodies.ReportOnce("place hand", e); return false; }
        }

        /// <summary>Lets a hand placed with <see cref="PlaceHand"/> go back to the pose. Since 5.15.0.</summary>
        public static void ReleaseHand(FruitBody body, bool left)
        {
            if (body == null) return;
            try { Placer(body, left)?.RemovePosition(); }
            catch (Exception e) { FruitBodies.ReportOnce("release hand", e); }
        }

        // ── Swing ──

        private static bool Swinging(FruitBody b)
        {
            for (int i = 0; i < _swings.Count; i++) if (_swings[i].Attacker == b) return true;
            return false;
        }

        private static void StartSwing(FruitBody attacker, FruitBody victim, FruitStrike kind, string source, float strength)
        {
            if (Swinging(attacker)) return;
            var s = new Swing
            {
                Attacker = attacker, Victim = victim, Kind = kind, Source = source, Strength = strength,
                StageAt = Time.time, Limb = PickLimb(victim, kind),
            };
            _swings.Add(s);

            try
            {
                if (kind == FruitStrike.Punch)
                {
                    var right = Placer(attacker, false);
                    var left = Placer(attacker, true);
                    // A held item swings with the hand that holds it (5.27.0); else the right
                    // hand, unless the game's covering is holding it to a wound.
                    int held = HeldHand(attacker);
                    bool useLeft = held >= 0 ? held == 1 : (right == null || Busy(right)) && left != null && !Busy(left);
                    s.Main = useLeft ? left : right;
                    s.MainHand = useLeft ? LeftHand : RightHand;
                    s.Stage = Stage.Chamber;
                    Frame(attacker, victim, out Vector3 chest, out Vector3 fwd, out Vector3 side);
                    Vector3 chamber = chest + side * (useLeft ? -0.2f : 0.2f) + fwd * 0.05f + Vector3.up * 0.08f;
                    s.MainPlaced = Place(attacker, s.Main, chamber, PunchHandSpeed * 0.6f);
                }
                else
                {
                    s.Main = Placer(attacker, false);
                    s.Off  = Placer(attacker, true);
                    s.MainHand = RightHand; s.OffHand = LeftHand;
                    Extend(s);
                }
            }
            catch (Exception e) { FruitBodies.ReportOnce("strike swing", e); }
        }

        /// <summary>Sends the hand(s) at the victim.</summary>
        private static void Extend(Swing s)
        {
            s.Stage = Stage.Extend;
            s.StageAt = Time.time;
            var a = s.Attacker; var v = s.Victim;
            if (s.Limb < 0 || !v.Limbs[s.Limb].Present) s.Limb = PickLimb(v, s.Kind);
            if (s.Limb < 0) return;

            Frame(a, v, out Vector3 chest, out Vector3 fwd, out Vector3 side);
            var limb = v.Limbs[s.Limb];
            s.Contact = NearestCollider(limb, chest, out Vector3 p) != null ? p : limb.LivePosition;
            Vector3 through = s.Contact - chest;
            through = through.sqrMagnitude > 1e-4f ? through.normalized : fwd;

            if (s.Kind == FruitStrike.Punch)
                s.MainPlaced |= Place(a, s.Main, s.Contact + through * 0.12f, PunchHandSpeed);
            else
            {
                Vector3 at = s.Contact + through * 0.1f + Vector3.up * 0.04f;
                s.MainPlaced |= Place(a, s.Main, at + side * 0.16f, ShoveHandSpeed);
                s.OffPlaced  |= Place(a, s.Off,  at - side * 0.16f, ShoveHandSpeed);
            }
        }

        private static void TickSwings()
        {
            float now = Time.time;
            for (int i = _swings.Count - 1; i >= 0; i--)
            {
                var s = _swings[i];
                bool done;
                try { done = Advance(s, now); }
                catch (Exception e) { FruitBodies.ReportOnce("strike", e); done = true; }
                if (!done) continue;
                LetGo(s);
                _swings.RemoveAt(i);
            }
        }

        /// <summary>One step of a swing; true when it is over.</summary>
        private static bool Advance(Swing s, float now)
        {
            var a = s.Attacker; var v = s.Victim;
            if (a.Creature == null || !a.Alive || !a.Conscious) return true;
            if (v.Creature == null || FruitBodies.Get(v.Id) != v) return true;
            float t = now - s.StageAt;

            switch (s.Stage)
            {
                case Stage.Chamber:
                    if (t >= WindUp) Extend(s);
                    return false;

                case Stage.Extend:
                {
                    // Lands when the fist (or either palm) gets to the struck point, or the time is up.
                    bool there = Near(a, s.MainHand, s.Contact) || (s.Kind == FruitStrike.Shove && Near(a, s.OffHand, s.Contact));
                    bool noHands = !s.MainPlaced && !s.OffPlaced;
                    if (there || t >= MaxExtendTime || (noHands && t >= WindUp))
                    {
                        Land(s, now);
                        s.Stage = Stage.Hold;
                        s.StageAt = now;
                    }
                    return false;
                }

                default:
                    return t >= (s.Kind == FruitStrike.Punch ? 0.12f : 0.25f);
            }
        }

        private static bool Near(FruitBody b, int hand, Vector3 point)
        {
            var l = b.Limbs[hand];
            return l.Present && (l.LivePosition - point).sqrMagnitude < 0.18f * 0.18f;
        }

        private static void LetGo(Swing s)
        {
            try { if (s.MainPlaced && s.Main != null && !s.Main.WasCollected) s.Main.RemovePosition(); } catch { }
            try { if (s.OffPlaced && s.Off != null && !s.Off.WasCollected) s.Off.RemovePosition(); } catch { }
            s.MainPlaced = s.OffPlaced = false;
        }

        // ── The blow ──

        private static void Land(Swing s, float now)
        {
            if (s.Landed) return;
            s.Landed = true;
            var a = s.Attacker; var v = s.Victim;

            string actor = FruitEvents.BodyActor(a.Id);
            string noun = s.Kind == FruitStrike.Punch ? "punch" : "shove";
            float reach = Distance(a, v);
            if (reach > Reach + 0.35f || s.Limb < 0 || !v.Limbs[s.Limb].Present)
            {
                FruitEvents.Publish(s.Source, "SwungAt", $"{a.Name} swung at {v.Name} and missed",
                                    v.HasPosition ? v.Position : (Vector3?)null, radius: 8f, body: v.Id, magnitude: 0.5f,
                                    detail: $"reach={reach:0.00}", actor: actor, cause: noun);
                return;
            }

            int li = s.Limb;
            var limb = v.Limbs[li];
            Frame(a, v, out Vector3 chest, out Vector3 fwd, out _);
            // Where the fist actually is, if it got close; else the nearest point to the chest.
            var fist = a.Limbs[s.MainHand];
            Vector3 from = fist.Present && (fist.LivePosition - limb.LivePosition).sqrMagnitude < 0.6f * 0.6f ? fist.LivePosition : chest;
            Collider col = NearestCollider(limb, from, out Vector3 contact);
            if (col == null) contact = limb.LivePosition;
            Vector3 dir = contact - chest;
            dir.y *= 0.3f;
            if (dir.sqrMagnitude < 1e-4f) dir = fwd;
            dir.Normalize();

            float mass = BodyMass(v);
            float strength = s.Strength;
            int torso = FruitBodies.Torso, pelvis = FruitBodies.Pelvis;
            float impulse;
            bool flinched;
            if (s.Kind == FruitStrike.Punch)
            {
                impulse = PunchSpeed * mass * strength;
                bool split = li != torso && v.Limbs[torso].Present;
                Push(limb, dir * (impulse * (split ? 0.7f : 1f)), contact);
                if (split) Push(v.Limbs[torso], dir * (impulse * 0.3f), v.Limbs[torso].LivePosition);
                flinched = Flinch(v, li, dir * (PunchReaction * strength), contact);

                if (PunchBruise > 0f && col != null)
                {
                    var receiver = FruitWounds.LimbOf(col);
                    if (receiver != null)
                        FruitWounds.Burst(receiver, contact - dir * 0.03f, dir, 1, -PunchBruise * strength, 1f, FruitWounds.NextStrikeHit(), _rng);
                }
            }
            else
            {
                impulse = ShoveSpeed * mass * strength;
                Push(limb, dir * (impulse * 0.55f), contact);
                if (li != pelvis) Push(v.Limbs[pelvis], dir * (impulse * 0.45f), v.Limbs[pelvis].LivePosition);
                flinched = Flinch(v, li, dir * (ShoveReaction * strength), contact);
            }

            v.LastStruckBy = a.Id;
            v.LastStruckAt = now;
            FruitBodies.Harmed(v, actor);

            string text = s.Kind == FruitStrike.Punch ? $"{a.Name} punched {v.Name} in the {limb.Name}" : $"{a.Name} shoved {v.Name}";
            float magnitude = s.Kind == FruitStrike.Punch ? (li == FruitBodies.Head ? 0.9f : 0.8f) : 0.75f;
            FruitEvents.Publish(s.Source, "Attacked", text, contact, radius: 10f, body: v.Id,
                                magnitude: magnitude, limb: li,
                                detail: $"impulse={impulse:0} mass={mass:0} reach={reach:0.00} hands={(s.MainPlaced || s.OffPlaced ? "ik" : "none")} flinch={(flinched ? "yes" : "no")}",
                                actor: actor, cause: noun, direction: dir);
        }

        private static void Push(FruitLimb l, Vector3 impulse, Vector3 at)
        {
            var rb = Rb(l);
            if (rb != null) rb.AddForceAtPosition(impulse, at, ForceMode.Impulse);
        }

        /// <summary>The body's mass, kg: its limb rigidbodies together (70 if none can be read).</summary>
        private static float BodyMass(FruitBody b)
        {
            float m = 0f;
            foreach (var l in b.Limbs)
            {
                var rb = Rb(l);
                if (rb == null) continue;
                try { m += rb.mass; } catch { }
            }
            return m > 1f ? m : 70f;
        }

        /// <summary>
        /// The victim's own flinch: its FinalIK hit reaction on the struck limb, as a pain spike
        /// would trigger it (PuppeteerPainPerception.OnPainSpike hits both the effector and the bone
        /// hit point registered for the limb). False if the limb has none.
        /// </summary>
        private static bool Flinch(FruitBody v, int limb, Vector3 force, Vector3 point)
        {
            if (force.sqrMagnitude < 1e-6f) return false;
            try
            {
                var hrp = v.Bind.Refs?.IK?.HitReactionProvider;
                if (hrp == null) return false;
                var key = (HumanoidNodeTagValue)limb;
                bool any = false;
                var effectors = hrp.EffectorHitPointsMap;
                if (effectors != null && effectors.TryGetValue(key, out var e) && e != null) { e.Hit(force, point); any = true; }
                var bones = hrp.BoneHitPointsMap;
                if (bones != null && bones.TryGetValue(key, out var b) && b != null) { b.Hit(force, point); any = true; }
                return any;
            }
            catch (Exception e) { FruitBodies.ReportOnce("hit reaction", e); return false; }
        }

        // ── Helpers ──

        internal static HandEffectorPlacer Placer(FruitBody b, bool left)
        {
            var ik = b.Bind.Refs?.IK;
            if (ik == null) return null;
            return left ? ik.LeftHandEffectorPlacer : ik.RightHandEffectorPlacer;
        }

        /// <summary>Whether the game's covering is using this hand (flying it, or holding it to a wound).</summary>
        internal static bool Busy(HandEffectorPlacer p)
        {
            try { return p.InFlight || (p.Activation != null && p.Activation.IsActivated); }
            catch { return false; }
        }

        /// <summary>Flies a hand's IK effector to a world point. The point is taken relative to the
        /// physical pelvis and placed the same way relative to the IK rig's pelvis.</summary>
        private static bool Place(FruitBody b, HandEffectorPlacer placer, Vector3 world, float speed)
        {
            if (placer == null || placer.WasCollected) return false;
            var rig = b.Bind.Refs?.IK?.FullBodyBiped?.references?.pelvis;
            if (rig == null) return false;
            Vector3 local = rig.InverseTransformPoint(rig.position + (world - LiveCentre(b)));
            placer.SetLocalPosition(local, rig, Mathf.Max(0.1f, speed), false,
                                    new Il2CppSystem.Nullable<Vector3>(), new Il2CppSystem.Nullable<Quaternion>(), null);
            return true;
        }

        /// <summary>The attacker's chest, which way the victim is from it (flat), and the attacker's right.</summary>
        private static void Frame(FruitBody a, FruitBody v, out Vector3 chest, out Vector3 fwd, out Vector3 right)
        {
            var t = a.Limbs[FruitBodies.Torso];
            chest = t.Present ? t.LivePosition : LiveCentre(a) + Vector3.up * 0.35f;
            fwd = LiveCentre(v) - LiveCentre(a);
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : a.Facing;
            right = Vector3.Cross(Vector3.up, fwd);
        }

        private static Rigidbody Rb(FruitLimb l)
        {
            if (l == null || !l.Present || l.Physics == null || l.Physics.WasCollected) return null;
            try { return l.Physics.m_rb; } catch { return null; }
        }

        /// <summary>Where a punch goes: mostly the head, else the torso; a shove goes to the torso.</summary>
        private static int PickLimb(FruitBody v, FruitStrike kind)
        {
            bool head = v.Limbs[FruitBodies.Head].Present, torso = v.Limbs[FruitBodies.Torso].Present;
            if (kind == FruitStrike.Punch && head && (!torso || _rng.NextDouble() < 0.65)) return FruitBodies.Head;
            if (torso) return FruitBodies.Torso;
            if (v.Limbs[FruitBodies.Pelvis].Present) return FruitBodies.Pelvis;
            return head ? FruitBodies.Head : -1;
        }

        private static Collider NearestCollider(FruitLimb limb, Vector3 from, out Vector3 point)
        {
            point = default;
            Collider best = null;
            float bestD = float.MaxValue;
            try
            {
                var cols = limb.Physics?.m_colliders;
                if (cols == null) return null;
                foreach (var c in cols)
                {
                    if (c == null || !c.enabled) continue;
                    Vector3 p = c.ClosestPoint(from);
                    float d = (p - from).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; point = p; }
                }
            }
            catch (Exception e) { FruitBodies.ReportOnce("limb colliders", e); }
            return best;
        }

        private static void ResetStrikes()
        {
            foreach (var s in _swings) LetGo(s);
            _swings.Clear();
        }
    }
}
