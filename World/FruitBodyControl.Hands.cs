using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>How a hand action ended. Since 5.27.0.</summary>
    public enum FruitHandsResult
    {
        /// <summary>The item is in the body's hand.</summary>
        PickedUp,
        Dropped,
        Thrown,
        /// <summary>A use was carried out (shots fired, a swing started, a mod's use ran).</summary>
        Used,
        /// <summary>Handed to another body (which then has it, with its own PickedUp).</summary>
        Given,
        /// <summary>Could not get to it, or could not get a hand to it.</summary>
        Unreachable,
        TooHeavy,
        /// <summary>Someone else has it now.</summary>
        Taken,
        /// <summary>The item, the hand or the grip is gone (destroyed, cut off, torn loose).</summary>
        Lost,
        Stopped,
        /// <summary>Asked for something the item or the body can't do.</summary>
        Failed,
    }

    // Hands. A body picks things up, carries them, throws them, uses them and hands them over.
    //
    // Picking up: the body walks to the item (a path, like any goto order), turns to it and
    // reaches with the game's hand IK. Ragdolls can't bend down to the floor, so once the hand is
    // close enough (MagnetReach, or right above it) or the body is standing at the item, the item
    // is pulled into the hand over a quarter of a second. That stands in until an animation for
    // reaching down exists.
    //
    // Holding: a FixedJoint from the item to the hand's rigidbody, and the item ignores every
    // collider of its holder (the 2026-10-11 hands test: joint alone, the item knocked against its
    // holder and sat 13 cm and 50 degrees off; with the colliders ignored it stayed within 1 cm).
    // The hand IK holds a carry pose relative to the rig pelvis.
    //
    // Aiming a gun: the hand IK can't set the hand's rotation, so the gun is re-seated in the
    // hand (joint off, turned about the grip, joint on) to point at the target, a few times a
    // second while aiming and right before every shot. Automatics fire shot by shot too (5.27.1;
    // 5.27.0 used the game's loop firing, and recoil walked the burst off between re-seats).
    // Shaking hands (pain, blood loss, balance) put each seat off by a few degrees. Up close the
    // arm is held back so the muzzle stays short of the target (5.27.1; a shotgun's barrel ended
    // up inside the body it was aimed at, up to 87 degrees off).
    public static partial class FruitBodyControl
    {
        /// <summary>How close the hand must get to an item before it is pulled in, metres; and how
        /// far right above it the hand may hang and still count (a standing body can't reach the
        /// floor). Since 5.27.0.</summary>
        public static float MagnetReach = 0.6f, MagnetAbove = 1.1f;

        /// <summary>Longest a body tries to get its hand to an item, seconds. Since 5.27.0.</summary>
        public static float ReachSeconds = 2.5f;

        /// <summary>Where a body stands to pick something up, flat metres from its pelvis to the
        /// nearest point of the item (<see cref="FruitLibConfig.PickupStance"/> and
        /// <see cref="FruitLibConfig.PickupStanceLow"/>). Standing right over it, the knees of the
        /// crouch and the reaching arm end up in the item, so a body that is closer than this
        /// minus <see cref="StanceTooClose"/> (or further than this plus <see cref="StanceTooFar"/>)
        /// first walks to a stance. Since 5.33.0.</summary>
        public static float StanceTooClose = 0.15f, StanceTooFar = 0.2f;

        /// <summary>How far from the shoulder the hand is sent: the arm's length. A target further
        /// off makes the hand IK strain at it and drag the body over (5.33.1: a floor item 0.7 m
        /// away, the body bounced and flipped), so the hand goes as far toward it as the arm reaches.
        /// And a body whose Balance falls under <see cref="ReachAbortBalance"/> while reaching lets go
        /// and gives up rather than flail. Since 5.33.1.</summary>
        public static float ArmReach = 0.62f, ReachAbortBalance = 60f;

        /// <summary>Goto orders to a stance stop within this of their point; the point is set that
        /// much beyond the stance along the way in, so the body ends on the stance itself.</summary>
        private const float StanceStop = 0.3f;

        /// <summary>Crouch (<see cref="FruitPoseShape.Crouch"/>) for an item more than this far below
        /// the pelvis, metres; then the hand must come within <see cref="CrouchMagnet"/> of the grip,
        /// and the reach may take <see cref="CrouchSeconds"/> longer. Since 5.31.0.</summary>
        public static bool CrouchToPickUp = true;
        public static float CrouchBelow = 0.3f, CrouchMagnet = 0.3f, CrouchSeconds = 1.5f;

        /// <summary>Heaviest item a body will pick up, kg; heavier items are held as if they
        /// weighed <see cref="HeldMassCap"/> (the arm can't hold a heavy joint level). Since 5.27.0.</summary>
        public static float CarryMaxMass = 25f, HeldMassCap = 8f;

        /// <summary>A held item weighs this much of its own mass (then capped at <see cref="HeldMassCap"/>),
        /// so a rifle doesn't drag the holder's balance off when walking; the mass is restored when
        /// it leaves the hand. 1 = full weight. Since 5.35.2.</summary>
        public static float HeldMassScale = 0.5f;

        /// <summary>Force that tears an item out of a hand, newtons. Since 5.27.0.</summary>
        public static float HoldBreakForce = 8000f;

        /// <summary>Drop what it holds when knocked down for over a second. Since 5.27.0.</summary>
        public static bool DropWhenDown = true;

        /// <summary>Throwing speed for a 2 kg item at strength 1, m/s (lighter goes faster). Since 5.27.0.</summary>
        public static float ThrowSpeed = 10f;

        /// <summary>A gun: seconds spent raising and aiming before the first shot, shots per use
        /// for one that fires single shots, and seconds of fire for an automatic. Since 5.27.0.</summary>
        public static float AimSeconds = 0.6f, BurstSeconds = 0.8f;
        public static int ShotsPerUse = 3;

        /// <summary>Raised when a hand action ends: picked up, dropped, thrown, used, given, or failed. Since 5.27.0.</summary>
        public static event Action<FruitBody, FruitWorldItem, FruitHandsResult> HandsEnded;

        private enum HandStage { Walk, Reach, Pull, Hold, WindUp, Release, Aim, Fire, GiveWalk }

        private sealed class Hands
        {
            public FruitBody Body;
            public FruitWorldItem Item;
            public HandStage Stage;
            public float StageAt, Until, NextPlace;
            public string Source = "FruitLib";
            public bool Left;
            public FixedJoint Joint;
            public Vector3 Grip;              // item space, rotation only, from the rigidbody
            public Quaternion Carry;          // item rotation in the body's facing frame
            public readonly List<(Collider a, Collider b)> Ignored = new List<(Collider, Collider)>();
            public bool MassCapped;
            /// <summary>Went down into a crouch for an item low down (FruitPose), and when.</summary>
            public bool Crouched, CrouchTried;
            public float CrouchAt;
            public Vector3 PullFromPos;
            public Quaternion PullFromRot;
            public float DownSince = -1f;
            // Standing where the reach works (5.33.0): the stance walked to, where the item was when
            // it was chosen, how many times it was chosen again, and whether the check at the item is done.
            public Vector3 Stance, ItemAt;
            public int Restances;
            public bool Settled;
            // Using it.
            public FruitBody Target;
            public Vector3 Point;
            public bool HasPoint;
            public int ShotsLeft;
            public float NextShot, NextSeat, FireUntil, ShotGap;
            /// <summary>Grip to muzzle, metres (a gun), measured when it is first aimed.</summary>
            public float MuzzleReach = -1f;
            public bool Looping;
            public float Strength = 1f;
            public FruitBody GiveTo;
            /// <summary>Aiming for a recipe: hold the aim, don't fire; Aimed once it has been held AimSeconds.</summary>
            public bool AimOnly, Aimed;
        }

        private static readonly Dictionary<int, Hands> _hands = new Dictionary<int, Hands>();
        private static readonly List<(List<(Collider a, Collider b)> pairs, float at)> _unignore = new List<(List<(Collider, Collider)>, float)>();
        private static readonly List<int> _handsDone = new List<int>();
        private static readonly List<Hands> _handsNow = new List<Hands>();

        // ── Asking ──

        /// <summary>What a body holds, or null. Since 5.27.0.</summary>
        public static FruitWorldItem Holding(FruitBody body)
            => body != null && _hands.TryGetValue(body.Id, out var h) && h.Joint != null && h.Stage >= HandStage.Hold ? h.Item : null;

        /// <summary>Whether a body is on its way to an item or reaching for it. Since 5.27.0.</summary>
        public static bool IsReaching(FruitBody body)
            => body != null && _hands.TryGetValue(body.Id, out var h) && h.Stage <= HandStage.Pull;

        /// <summary>The item a body is going for, holding or using, or null. Since 5.27.0.</summary>
        public static FruitWorldItem HandsOn(FruitBody body)
            => body != null && _hands.TryGetValue(body.Id, out var h) ? h.Item : null;

        /// <summary>Where a body aiming a gun is aiming (the target's chest, or the point), or null
        /// if it isn't aiming. Since 5.27.1.</summary>
        public static Vector3? AimTarget(FruitBody body)
            => body != null && _hands.TryGetValue(body.Id, out var h) && (h.Stage == HandStage.Aim || h.Stage == HandStage.Fire) ? TargetPoint(h) : (Vector3?)null;

        /// <summary>Whether a body holding a gun is pointing it at a point (within the angle). Since 5.27.0.</summary>
        public static bool AimingAt(FruitBody body, Vector3 point, float degrees = 8f)
        {
            var it = Holding(body);
            if (it == null || !it.IsGun) return false;
            if (!_hands.TryGetValue(body.Id, out var h) || (h.Stage != HandStage.Aim && h.Stage != HandStage.Fire)) return false;
            try
            {
                var muzzle = Muzzle(it);
                return Vector3.Angle(muzzle.forward, point - muzzle.position) < degrees;
            }
            catch { return false; }
        }

        // ── Orders ──

        /// <summary>
        /// Walks a body to an item and picks it up (dropping whatever it held). Ends with
        /// <see cref="HandsEnded"/>: PickedUp, Unreachable, TooHeavy, Taken or Lost. False if it
        /// can't even start (no item, held by someone else, too heavy, body down). Since 5.27.0.
        /// </summary>
        public static bool PickUp(FruitBody body, FruitWorldItem item, bool run = false, string source = "FruitLib", float seconds = 30f)
        {
            if (body == null || !body.Alive || !body.Conscious || item == null || !item.Valid) return false;
            if (item.Holder != null && item.Holder != body) { HandsEnd(body, item, FruitHandsResult.Taken); return false; }
            if (item.Mass > CarryMaxMass) { HandsEnd(body, item, FruitHandsResult.TooHeavy); return false; }
            if (!HasHand(body)) return false;
            if (_hands.TryGetValue(body.Id, out var old))
            {
                if (old.Item == item && old.Stage >= HandStage.Hold) return true;
                Detach(old, FruitHandsResult.Dropped, publish: old.Joint != null);
            }

            var h = new Hands { Body = body, Item = item, Source = Src(source), Left = !body.Limbs[RightHand].Present };
            _hands[body.Id] = h;
            h.Until = Time.time + Mathf.Max(3f, seconds);
            float want = StanceFor(h, body);
            float dist = ItemDistance(item, LiveCentre(body), out _);
            if (dist < want - StanceTooClose || dist > want + StanceTooFar)
            {
                // Not within reach, or standing on it: to a stance beside it (not onto it) first.
                SetStage(h, HandStage.Walk);
                if (!WalkToStance(h, body, run, seconds)) GoTo(body, item.Position, want, seconds, run);
            }
            else SetStage(h, HandStage.Reach);
            return true;
        }

        /// <summary>Puts down what a body holds (or stops it going for something). Since 5.27.0.</summary>
        public static bool Drop(FruitBody body, string source = "FruitLib")
        {
            if (body == null || !_hands.TryGetValue(body.Id, out var h)) return false;
            if (h.Joint == null) { if (h.Stage == HandStage.Walk) Stop(body); End(h, FruitHandsResult.Stopped); return true; }
            h.Source = Src(source);
            Detach(h, FruitHandsResult.Dropped, publish: true);
            return true;
        }

        /// <summary>
        /// Throws what a body holds at a point (an arc that lands there if it can, given the
        /// speed). <paramref name="strength"/> scales the speed. Ends Thrown. Since 5.27.0.
        /// </summary>
        public static bool Throw(FruitBody body, Vector3 at, float strength = 1f, string source = "FruitLib")
        {
            var h = Held(body);
            if (h == null || !Upright(body)) return false;
            if (!h.Item.Can(FruitAffords.Throw) && h.Item.Mass > FruitItems.ThrowMaxMass) { HandsEnd(body, h.Item, FruitHandsResult.Failed); return false; }
            // Thrown means thrown live when the item arms before a throw (5.30.0).
            if (FruitHandling.ArmsBeforeThrow(h.Item.Handling))
                return StartRecipe(body, h, h.Item, null, at, false);
            return ThrowNow(body, h, at, strength, source);
        }

        private static bool ThrowNow(FruitBody body, Hands h, Vector3 at, float strength, string source)
        {
            h.Source = Src(source);
            h.Point = at; h.HasPoint = true;
            h.Strength = Mathf.Clamp(strength, 0.2f, 2f);
            Face(body, at - body.Position, 1f);
            SetStage(h, HandStage.WindUp);
            return true;
        }

        /// <summary>
        /// Uses what a body holds on another body or a point: a gun is raised, aimed and fired
        /// (<see cref="ShotsPerUse"/> shots, or a burst); a thing to swing goes for the target
        /// with it; a mod's item runs its <see cref="FruitWorldItem.OnUse"/>. Ends Used, or Failed
        /// if the item can't be used that way. Since 5.27.0.
        /// </summary>
        public static bool Use(FruitBody body, FruitBody target, Vector3? point = null, string source = "FruitLib", bool run = false)
        {
            var h = Held(body);
            if (h == null) return false;
            var it = h.Item;
            h.Source = Src(source);

            // The item says how it is handled (5.30.0): a grenade is armed and thrown, a mine put down.
            if (it.Handling != null && it.Handling.Length > 0)
                return StartRecipe(body, h, it, target, point, run);

            // A gun first: a mod's gun may also have an OnUse, for the interaction key.
            if (it.IsGun)
            {
                if (!Upright(body)) return false;
                h.Target = target;
                h.HasPoint = point.HasValue || target != null;
                h.Point = point ?? Vector3.zero;
                if (!h.HasPoint) { HandsEnd(body, it, FruitHandsResult.Failed); return false; }
                SetStage(h, HandStage.Aim);
                return true;
            }
            if (it.OnUse != null)
            {
                bool ok = false;
                try { ok = it.OnUse(body, it, target, point); } catch (Exception e) { FruitBodies.ReportOnce("item OnUse", e); }
                HandsEnd(body, it, ok ? FruitHandsResult.Used : FruitHandsResult.Failed);
                return ok;
            }
            if (it.Can(FruitAffords.Swing) && target != null)
            {
                bool ok = Attack(body, target, FruitStrike.Punch, 10f, run, h.Source, 1.3f);
                HandsEnd(body, it, ok ? FruitHandsResult.Used : FruitHandsResult.Failed);
                return ok;
            }
            if (it.Can(FruitAffords.Throw) && (target != null || point.HasValue))
                return Throw(body, target != null ? target.LiveHeadPosition : point.Value, 1f, source);
            HandsEnd(body, it, FruitHandsResult.Failed);
            return false;
        }

        /// <summary>Walks over to another body and hands it what this one holds. Ends Given (and
        /// PickedUp for the other), or Failed / Unreachable. Since 5.27.0.</summary>
        public static bool Give(FruitBody body, FruitBody to, bool run = false, string source = "FruitLib")
        {
            var h = Held(body);
            if (h == null || to == null || to == body || !to.Alive) return false;
            if (Holding(to) != null || !HasHand(to)) { HandsEnd(body, h.Item, FruitHandsResult.Failed); return false; }
            h.Source = Src(source);
            h.GiveTo = to;
            h.Until = Time.time + 20f;
            SetStage(h, HandStage.GiveWalk);
            if (Distance(body, to) > 1.2f) Approach(body, to, 0.9f, 20f, run);
            return true;
        }

        // ── Each frame ──

        private static void TickHands()
        {
            if (_unignore.Count > 0) Unignore(false);
            if (_recipes.Count > 0) TickRecipes();
            if (_hands.Count == 0) return;
            float now = Time.time;
            _handsDone.Clear();
            _handsNow.Clear();
            _handsNow.AddRange(_hands.Values);
            foreach (var h in _handsNow)
            {
                // Ended (or replaced) by an earlier one this frame: a hand-over, a drop.
                if (!_hands.TryGetValue(h.Body.Id, out var cur) || cur != h) continue;
                try { TickHand(h, now); }
                catch (Exception e) { FruitBodies.ReportOnce("hands", e); _handsDone.Add(h.Body.Id); }
            }
            foreach (int id in _handsDone)
                if (_hands.TryGetValue(id, out var h)) End(h, FruitHandsResult.Lost);
        }

        private static void TickHand(Hands h, float now)
        {
            var b = h.Body;
            var it = h.Item;
            if (b.Creature == null || FruitBodies.Get(b.Id) != b) { Detach(h, FruitHandsResult.Lost, publish: false); return; }
            if (!it.Valid) { End(h, FruitHandsResult.Lost); return; }
            if (h.Stage < HandStage.Hold && it.Holder != null && it.Holder != b) { if (h.Stage == HandStage.Walk) Stop(b); End(h, FruitHandsResult.Taken); return; }
            if (!b.Alive || !b.Conscious || !HasHand(b, h.Left))
            {
                if (h.Joint != null) Detach(h, FruitHandsResult.Lost, publish: true);
                else End(h, FruitHandsResult.Lost);
                return;
            }
            float t = now - h.StageAt;

            switch (h.Stage)
            {
                case HandStage.Walk:
                {
                    // The order brings it to its stance and ends there; the check at the item is in Reach.
                    float away = ItemDistance(it, LiveCentre(b), out _);
                    if (!IsWalking(b) || now > h.Until)
                    {
                        if (away <= StanceFor(h, b) + 1.0f) { if (IsWalking(b)) Stop(b); SetStage(h, HandStage.Reach); }
                        else { if (IsWalking(b)) Stop(b); End(h, FruitHandsResult.Unreachable); }
                        return;
                    }
                    // Something moved the item (a push, a throw): the stance is chosen again.
                    if (FruitNav.Flat(it.Position - h.ItemAt) > 0.8f && h.Restances < 3 && now >= h.NextPlace)
                    {
                        h.NextPlace = now + 0.5f;
                        h.Restances++;
                        WalkToStance(h, b, IsRunning(b), Mathf.Max(3f, h.Until - now));
                    }
                    return;
                }

                case HandStage.Reach:
                {
                    // On its stance, and turned to the item: is it where the reach works? Close in on it
                    // (standing over it ruins the crouch) or short of it, it walks to a stance once more.
                    if (!h.Settled)
                    {
                        if (IsTurning(b) && t < 0.8f) return;
                        h.Settled = true;
                        float want = StanceFor(h, b), near = ItemDistance(it, LiveCentre(b), out _);
                        FruitLog.Info($"[FruitHands] #{b.Id} at {it.Name}: {near:0.00} m off it (stance {want:0.00}), item {it.Position.y - LiveCentre(b).y:+0.00;-0.00} m from the pelvis, balance {b.Balance:0}, restance {h.Restances}.");
                        if ((near < want - StanceTooClose || near > want + StanceTooFar) && h.Restances < 2 && Upright(b))
                        {
                            h.Restances++;
                            h.Until = now + 8f;
                            SetStage(h, HandStage.Walk);
                            if (WalkToStance(h, b, false, 8f)) return;
                            SetStage(h, HandStage.Reach);   // nowhere better: reach from here
                            h.Settled = true;
                            t = 0f;
                        }
                    }
                    Vector3 grip = GripWorld(h, b);
                    // Off its feet while reaching (the hand pulled it over): let go, don't flail about.
                    if (b.Balance < ReachAbortBalance || !Upright(b))
                    {
                        FruitLog.Info($"[FruitHands] #{b.Id} gave up reaching for {it.Name}: balance {b.Balance:0}, {(Upright(b) ? "upright" : "down")}, {t:0.0} s into the reach.");
                        ReleaseHand(b, h.Left);
                        End(h, FruitHandsResult.Failed);
                        return;
                    }
                    // Low down: crouch to it once the turn to face it is (mostly) done.
                    if (CrouchToPickUp && !h.CrouchTried && grip.y < LiveCentre(b).y - CrouchBelow && (!IsTurning(b) || t > 0.6f))
                    {
                        h.CrouchTried = true;
                        if (IsTurning(b)) Stop(b);
                        if (FruitPose.Hold(b, FruitPoseShape.Crouch, 0.5f, source: h.Source)) { h.Crouched = true; h.CrouchAt = now; }
                    }
                    if (now >= h.NextPlace) { h.NextPlace = now + 0.4f; PlaceHand(b, h.Left, Reachable(h, b, grip), 1.6f); }
                    var hand = b.Limbs[h.Left ? LeftHand : RightHand];
                    Vector3 hp = hand.LivePosition;
                    bool close = h.Crouched
                        ? FruitPose.Weight(b) >= 0.9f && (hp - grip).sqrMagnitude <= CrouchMagnet * CrouchMagnet
                        : (hp - grip).sqrMagnitude <= MagnetReach * MagnetReach
                          || (FruitNav.Flat(hp - grip) <= 0.45f && hp.y >= grip.y - 0.1f && hp.y - grip.y <= MagnetAbove);
                    if (close) { SetStage(h, HandStage.Pull); return; }
                    if (t >= ReachSeconds + (h.Crouched ? CrouchSeconds : 0f))
                    {
                        // Standing right at it: the hand just can't bend that far down.
                        FruitLog.Info($"[FruitHands] #{b.Id} reach for {it.Name} ran out: hand {Vector3.Distance(hp, grip):0.00} m off the grip, {FruitNav.Flat(hp - grip):0.00} m across and {hp.y - grip.y:+0.00;-0.00} m up, pelvis {ItemDistance(it, LiveCentre(b), out _):0.00} m off the item, crouched {h.Crouched}.");
                        if (Upright(b) && FruitNav.Flat(it.Position - LiveCentre(b)) <= 1.0f) SetStage(h, HandStage.Pull);
                        else { ReleaseHand(b, h.Left); End(h, FruitHandsResult.Unreachable); }
                    }
                    return;
                }

                case HandStage.Pull:
                {
                    var handRb = HandRb(b, h.Left);
                    if (handRb == null) { End(h, FruitHandsResult.Lost); return; }
                    float k = Mathf.Clamp01(t / 0.25f);
                    k = k * k * (3f - 2f * k);
                    Quaternion rot = Quaternion.Slerp(h.PullFromRot, CarryRotation(h, b), k);
                    Vector3 to = handRb.position - rot * h.Grip;
                    Vector3 pos = Vector3.Lerp(h.PullFromPos, to, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.08f);
                    var rb = it.Rb;
                    rb.transform.SetPositionAndRotation(pos, rot);
                    rb.position = pos;
                    rb.rotation = rot;
                    if (k >= 1f) Grab(h, b, handRb);
                    return;
                }

                case HandStage.Hold:
                    if (!Holds(h)) return;
                    if (!Upright(b))
                    {
                        if (h.DownSince < 0f) h.DownSince = now;
                        else if (DropWhenDown && now - h.DownSince > 1f) { Detach(h, FruitHandsResult.Dropped, publish: true, why: "fell"); return; }
                    }
                    else h.DownSince = -1f;
                    if (Swinging(b)) { h.NextPlace = now + 0.3f; return; }
                    if (now >= h.NextPlace && Upright(b)) PlaceCarry(h, b);
                    return;

                case HandStage.WindUp:
                    if (!Holds(h)) return;
                    if (t >= 0.35f) SetStage(h, HandStage.Release);
                    return;

                case HandStage.Release:
                    if (!Holds(h)) return;
                    if (t >= 0.08f) Launch(h, b);
                    return;

                case HandStage.Aim:
                    if (!Holds(h)) return;
                    if (!Upright(b)) { SetStage(h, HandStage.Hold); HandsEnd(b, it, FruitHandsResult.Failed); return; }
                    if (now >= h.NextPlace) { h.NextPlace = now + 0.2f; PlaceHand(b, h.Left, AimPoint(h, b), 2.5f); }
                    if (t >= 0.3f && now >= h.NextSeat) { h.NextSeat = now + 0.2f; SeatAt(h, b, 0f); }
                    if (t >= AimSeconds)
                    {
                        if (h.AimOnly) h.Aimed = true;
                        else SetStage(h, HandStage.Fire);
                    }
                    return;

                case HandStage.Fire:
                    if (!Holds(h)) return;
                    if (!Upright(b)) { StopFire(h); SetStage(h, HandStage.Hold); HandsEnd(b, it, FruitHandsResult.Used); return; }
                    if (now >= h.NextPlace) { h.NextPlace = now + 0.15f; PlaceHand(b, h.Left, AimPoint(h, b), 2.5f); }
                    if (now >= h.NextShot)
                    {
                        if (h.ShotsLeft <= 0) { SetStage(h, HandStage.Hold); HandsEnd(b, it, FruitHandsResult.Used); return; }
                        SeatAt(h, b, Shake(b));
                        h.ShotsLeft--;
                        if (it.ModGun != null) FruitItems.FireModGun(it, b);
                        else try { it.Gun.Shoot(); } catch (Exception e) { FruitBodies.ReportOnce("gun shoot", e); }
                        h.NextShot = now + h.ShotGap + (h.Looping ? 0f : UnityEngine.Random.Range(0f, 0.15f));
                    }
                    return;

                case HandStage.GiveWalk:
                {
                    if (!Holds(h)) return;
                    var to = h.GiveTo;
                    if (to == null || !to.Alive || FruitBodies.Get(to.Id) != to) { SetStage(h, HandStage.Hold); HandsEnd(b, it, FruitHandsResult.Failed); return; }
                    if (Distance(b, to) <= 1.3f) { HandOver(h, b, to); return; }
                    if (now > h.Until || (!IsWalking(b) && t > 1f)) { SetStage(h, HandStage.Hold); HandsEnd(b, it, FruitHandsResult.Unreachable); }
                    return;
                }
            }
        }

        // ── Stages ──

        private static void SetStage(Hands h, HandStage s)
        {
            var b = h.Body;
            h.Stage = s;
            h.StageAt = Time.time;
            h.NextPlace = 0f;
            Frame(b, out Vector3 chest, out Vector3 fwd, out Vector3 right);
            float side = h.Left ? -1f : 1f;
            switch (s)
            {
                case HandStage.Reach:
                    h.Settled = false;
                    // Knees and elbows don't kick it about while the body gets down to it (5.33.0).
                    if (h.Ignored.Count == 0) IgnoreHolder(h, true);
                    Face(b, h.Item.Position - b.Position, 1.2f);
                    break;
                case HandStage.Pull:
                {
                    var rb = h.Item.Rb;
                    h.Grip = h.Item.Grip ?? AutoGrip(h.Item, b, h.Left);
                    h.Carry = CarryRotationLocal(h.Item, b);
                    h.PullFromPos = rb.position;
                    h.PullFromRot = rb.rotation;
                    try
                    {
                        var hl = b.Limbs[h.Left ? LeftHand : RightHand];
                        FruitLog.Info($"[FruitHands] #{b.Id} pulls {h.Item.Name}: hand {(hl.Present ? Vector3.Distance(hl.LivePosition, GripWorld(h, b)) : -1f):0.00} m off the grip, pelvis {ItemDistance(h.Item, LiveCentre(b), out _):0.00} m off the item, crouched {h.Crouched}, balance {b.Balance:0}.");
                    }
                    catch { }
                    if (h.Ignored.Count == 0) IgnoreHolder(h, true);
                    rb.isKinematic = true;
                    break;
                }
                case HandStage.WindUp:
                    PlaceHand(b, h.Left, chest + Vector3.up * 0.3f + right * side * 0.25f - fwd * 0.2f, 2f);
                    break;
                case HandStage.Release:
                    PlaceHand(b, h.Left, chest + Vector3.up * 0.25f + Flat(h.Point - chest, fwd) * 0.6f, 6f);
                    break;
                case HandStage.Aim:
                    Face(b, TargetPoint(h) - b.Position, AimSeconds + 4f);
                    h.NextSeat = 0f;
                    break;
                case HandStage.Fire:
                {
                    // Shot by shot, re-seated each time; an automatic just shoots faster, for BurstSeconds.
                    bool auto = false;
                    float delay = 0.3f;
                    var modGun = h.Item.ModGun;
                    if (modGun != null) { auto = modGun.Automatic; delay = modGun.ShotGap; }
                    else
                    {
                        try { auto = h.Item.Gun.IsAutomatic; } catch { }
                        try { delay = h.Item.Gun.FireDelay; } catch { }
                    }
                    h.Looping = false;
                    h.ShotGap = auto ? Mathf.Max(0.08f, delay) : Mathf.Max(0.3f, delay);
                    h.ShotsLeft = auto ? Mathf.Max(2, Mathf.CeilToInt(BurstSeconds / h.ShotGap)) : Mathf.Max(1, ShotsPerUse);
                    h.NextShot = 0f;
                    break;
                }
                case HandStage.Hold:
                    h.NextPlace = Time.time + 0.4f;
                    break;
            }
        }

        /// <summary>The pull is done: fix the item to the hand.</summary>
        private static void Grab(Hands h, FruitBody b, Rigidbody handRb)
        {
            var it = h.Item;
            var rb = it.Rb;
            Quaternion rot = CarryRotation(h, b);
            Vector3 pos = handRb.position - rot * h.Grip;
            rb.transform.SetPositionAndRotation(pos, rot);
            rb.position = pos;
            rb.rotation = rot;
            rb.isKinematic = false;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            float held = Mathf.Min(rb.mass * Mathf.Clamp01(HeldMassScale), HeldMassCap);
            if (held < rb.mass) { rb.mass = Mathf.Max(0.05f, held); h.MassCapped = true; }
            Joint(h, handRb);
            it.Holder = b;
            Uncrouch(h, 0.7f);
            SetStage(h, HandStage.Hold);
            PlaceCarry(h, b);
            FruitEvents.Publish(h.Source, "PickedUp", $"{b.Name} picked up {it.WithArticle}", it.Position, radius: 10f, body: b.Id,
                                magnitude: it.IsGun ? 0.5f : 0.25f, detail: $"mass={it.Mass:0.0} affords={it.Affords}",
                                actor: FruitEvents.BodyActor(b.Id), cause: it.Name);
            HandsEnd(b, it, FruitHandsResult.PickedUp);
        }

        private static void Joint(Hands h, Rigidbody handRb)
        {
            var j = h.Item.Rb.gameObject.AddComponent<FixedJoint>();
            j.connectedBody = handRb;
            j.breakForce = HoldBreakForce;
            h.Joint = j;
        }

        /// <summary>Whether the item is still fixed to the hand; ends the hold if not.</summary>
        private static bool Holds(Hands h)
        {
            if (h.Joint != null && !h.Joint.WasCollected) return true;
            h.Joint = null;
            Detach(h, FruitHandsResult.Lost, publish: true, why: "torn loose");
            return false;
        }

        /// <summary>Lets go: the joint off, the mass back, the hand free, the holder's colliders
        /// back on a moment later (so the item doesn't burst out of the body).</summary>
        private static void Detach(Hands h, FruitHandsResult result, bool publish, string why = null)
        {
            var b = h.Body;
            var it = h.Item;
            StopFire(h);
            try { if (h.Joint != null && !h.Joint.WasCollected) Object.DestroyImmediate(h.Joint); } catch { }
            h.Joint = null;
            if (it.Valid)
            {
                try
                {
                    if (it.Rb.isKinematic) it.Rb.isKinematic = false;
                    if (h.MassCapped) it.Rb.mass = it.Mass;
                }
                catch { }
            }
            h.MassCapped = false;
            if (h.Ignored.Count > 0) _unignore.Add((new List<(Collider, Collider)>(h.Ignored), Time.time + 0.6f));
            h.Ignored.Clear();
            if (it.Holder == b) it.Holder = null;
            ReleaseHand(b, h.Left);
            if (publish && it.Valid && result != FruitHandsResult.Thrown)
                FruitEvents.Publish(h.Source, "Dropped", $"{b.Name} dropped {it.WithArticle}{(why != null ? $" ({why})" : "")}",
                                    it.Position, radius: 8f, body: b.Id, magnitude: 0.2f, detail: why,
                                    actor: FruitEvents.BodyActor(b.Id), cause: it.Name);
            End(h, result);
        }

        private static void End(Hands h, FruitHandsResult result)
        {
            if (_hands.TryGetValue(h.Body.Id, out var cur) && cur == h) _hands.Remove(h.Body.Id);
            Uncrouch(h, 0.5f);
            if (h.Stage <= HandStage.Pull)
            {
                try { if (h.Item.Valid && h.Item.Rb.isKinematic && h.Stage == HandStage.Pull) h.Item.Rb.isKinematic = false; } catch { }
                if (h.Ignored.Count > 0) { _unignore.Add((new List<(Collider, Collider)>(h.Ignored), Time.time)); h.Ignored.Clear(); }
            }
            HandsEnd(h.Body, h.Item, result);
        }

        /// <summary>Back up out of the pickup crouch (only the crouch this hand action took).</summary>
        private static void Uncrouch(Hands h, float seconds)
        {
            if (!h.Crouched) return;
            h.Crouched = false;
            if (FruitPose.Current(h.Body) == FruitPoseShape.Crouch) FruitPose.Release(h.Body, seconds);
        }

        private static void HandsEnd(FruitBody b, FruitWorldItem it, FruitHandsResult r)
        {
            if (HandsEnded == null) return;
            try { HandsEnded(b, it, r); }
            catch (Exception e) { FruitBodies.ReportOnce("HandsEnded handler", e); }
        }

        // ── Throwing ──

        private static void Launch(Hands h, FruitBody b)
        {
            var it = h.Item;
            var rb = it.Rb;
            Vector3 from = rb.position;
            float mass = Mathf.Max(0.1f, it.Mass);
            float speed = ThrowSpeed * h.Strength * Mathf.Clamp(Mathf.Sqrt(2f / mass), 0.55f, 1.4f);
            Vector3 v = Ballistic(from, h.Point, speed);
            Vector3 handVel = Vector3.zero;
            try { handVel = HandRb(b, h.Left)?.linearVelocity ?? Vector3.zero; } catch { }

            var item = it;
            Detach(h, FruitHandsResult.Thrown, publish: false);
            if (!item.Valid) return;
            rb.linearVelocity = v + handVel * 0.3f;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 4f;
            FruitEvents.Publish(h.Source, "Thrown", $"{b.Name} threw {item.WithArticle}", from, radius: 15f, body: b.Id,
                                magnitude: 0.5f, detail: $"speed={v.magnitude:0.0} at={Vector3.Distance(from, h.Point):0.0}m",
                                actor: FruitEvents.BodyActor(b.Id), cause: item.Name, direction: v.normalized);
        }

        /// <summary>A launch velocity of the given speed that lands on the target, the lower of the
        /// two arcs; 40 degrees up if out of range.</summary>
        private static Vector3 Ballistic(Vector3 from, Vector3 to, float speed)
        {
            Vector3 d = to - from;
            Vector3 flat = new Vector3(d.x, 0f, d.z);
            float x = flat.magnitude, y = d.y, g = 9.81f, v2 = speed * speed;
            Vector3 dir = x > 1e-3f ? flat / x : Vector3.forward;
            float disc = v2 * v2 - g * (g * x * x + 2f * y * v2);
            float angle = disc >= 0f ? Mathf.Atan((v2 - Mathf.Sqrt(disc)) / (g * Mathf.Max(0.01f, x))) : 40f * Mathf.Deg2Rad;
            return (dir * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)) * speed;
        }

        // ── Handing over ──

        private static void HandOver(Hands h, FruitBody from, FruitBody to)
        {
            var it = h.Item;
            string src = h.Source;
            Detach(h, FruitHandsResult.Given, publish: false);
            if (!it.Valid) return;
            FruitEvents.Publish(src, "Gave", $"{from.Name} handed {it.WithArticle} to {to.Name}", it.Position, radius: 10f, body: to.Id,
                                magnitude: it.IsGun ? 0.5f : 0.3f, actor: FruitEvents.BodyActor(from.Id), cause: it.Name);
            var r = new Hands { Body = to, Item = it, Source = src, Left = !to.Limbs[RightHand].Present };
            _hands[to.Id] = r;
            SetStage(r, HandStage.Pull);
        }

        // ── Guns ──

        /// <summary>Turns the gun about its grip so the muzzle points at the target (plus shake),
        /// and fixes it there again.</summary>
        private static void SeatAt(Hands h, FruitBody b, float shakeDegrees)
        {
            var it = h.Item;
            var handRb = HandRb(b, h.Left);
            if (handRb == null || (!it.IsGun && !h.AimOnly)) return;
            Transform muzzle = Muzzle(it);
            var rb = it.Rb;
            Vector3 target = TargetPoint(h);
            Vector3 dir = target - muzzle.position;
            if (dir.sqrMagnitude < 0.01f) return;
            if (shakeDegrees > 0f) dir = Quaternion.Euler(UnityEngine.Random.Range(-shakeDegrees, shakeDegrees), UnityEngine.Random.Range(-shakeDegrees, shakeDegrees), 0f) * dir;
            Quaternion rot;
            if (it.Breech != null && !it.Breech.WasCollected)
            {
                // The bore is the breech-to-muzzle line (a launcher tube): the least turn onto the target.
                Vector3 bore = muzzle.position - it.Breech.position;
                rot = bore.sqrMagnitude > 1e-4f ? Quaternion.FromToRotation(bore, dir) * rb.rotation : rb.rotation;
            }
            else
            {
                Quaternion rel = Quaternion.Inverse(rb.rotation) * muzzle.rotation;
                rot = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Inverse(rel);
            }
            Vector3 gripWorld = rb.position + rb.rotation * h.Grip;
            Vector3 pos = gripWorld - rot * h.Grip;
            try { if (h.Joint != null) Object.DestroyImmediate(h.Joint); } catch { }
            rb.transform.SetPositionAndRotation(pos, rot);
            rb.position = pos;
            rb.rotation = rot;
            rb.angularVelocity = Vector3.zero;
            Joint(h, handRb);
        }

        private static void StopFire(Hands h)
        {
            if (!h.Looping) return;
            h.Looping = false;
            try { if (h.Item.Gun != null && !h.Item.Gun.WasCollected) h.Item.Gun.SetLoopFiring(false); } catch { }
        }

        /// <summary>How far off a body's aim wanders, degrees: steady when well, all over the
        /// place when hurt, bleeding or off balance.</summary>
        public static float Shake(FruitBody b)
            => 1f + b.Pain * 0.06f + Mathf.Max(0f, 100f - b.Blood) * 0.08f + (b.Balance < 60f ? 3f : 0f);

        private static Vector3 TargetPoint(Hands h)
        {
            var t = h.Target;
            if (t != null && t.HasPosition)
            {
                var torso = t.Limbs[FruitBodies.Torso];
                return torso.Present ? torso.LivePosition + Vector3.up * 0.1f : t.LiveHeadPosition;
            }
            return h.Point;
        }

        private static Vector3 AimPoint(Hands h, FruitBody b)
        {
            Frame(b, out Vector3 chest, out Vector3 fwd, out Vector3 right);
            Vector3 shoulder = chest + Vector3.up * 0.15f + right * (h.Left ? -0.1f : 0.1f);
            Vector3 to = TargetPoint(h) - shoulder;
            float dist = to.magnitude;
            Vector3 dir = dist > 0.1f ? to / dist : fwd;
            // Arm out to 0.5 m, but the muzzle stays 0.35 m short of the target.
            if (h.MuzzleReach < 0f) h.MuzzleReach = MuzzleReachOf(h);
            float arm = Mathf.Clamp(dist - h.MuzzleReach - 0.35f, 0.1f, 0.5f);
            return shoulder + dir * arm;
        }

        private static float MuzzleReachOf(Hands h)
        {
            try
            {
                var rb = h.Item.Rb;
                Vector3 grip = rb.position + rb.rotation * h.Grip;
                return Vector3.Distance(grip, Muzzle(h.Item).position);
            }
            catch { return 0.3f; }
        }

        private static Transform Muzzle(FruitWorldItem it)
        {
            Transform m = null;
            if (it.Muzzle != null && !it.Muzzle.WasCollected) return it.Muzzle;
            if (it.ModGun?.Muzzle != null) return it.ModGun.Muzzle;
            try { m = it.Gun?.Muzzle?.pivot; } catch { }
            return m != null ? m : it.Rb.transform;
        }

        // ── Poses ──

        private static void PlaceCarry(Hands h, FruitBody b)
        {
            h.NextPlace = Time.time + 1.5f;
            Frame(b, out Vector3 chest, out Vector3 fwd, out Vector3 right);
            float side = h.Left ? -1f : 1f;
            Vector3 at = h.Item.IsGun
                ? chest + fwd * 0.38f + right * side * 0.14f - Vector3.up * 0.05f
                : chest + fwd * 0.32f + right * side * 0.2f - Vector3.up * 0.12f;
            PlaceHand(b, h.Left, at, 1.5f);
        }

        /// <summary>Where the hand goes on the item, in the item's space (rotation only).</summary>
        private static Vector3 AutoGrip(FruitWorldItem it, FruitBody b, bool left)
        {
            var rb = it.Rb;
            var cols = it.Colliders;
            Vector3 centre = it.Position;
            Vector3 grip;
            if (it.IsGun)
            {
                Transform muzzle = Muzzle(it);
                Vector3 back = muzzle.forward, down = -muzzle.up;
                grip = centre - back * Extent(cols, centre, back) * 0.45f + down * Extent(cols, centre, down) * 0.6f;
            }
            else if (LongAxis(rb.transform, cols, centre, out Vector3 axis, out float half, out float ratio) && ratio > 2f)
                grip = centre - axis * half * 0.7f;
            else
            {
                var hand = b.Limbs[left ? LeftHand : RightHand];
                grip = ClosestOn(cols, hand.Present ? hand.LivePosition : b.Position + Vector3.up, centre);
            }
            return Quaternion.Inverse(rb.rotation) * (grip - rb.position);
        }

        /// <summary>How the item sits in the hands, relative to the body's facing frame.</summary>
        private static Quaternion CarryRotationLocal(FruitWorldItem it, FruitBody b)
        {
            if (it.CarryRotation.HasValue) return it.CarryRotation.Value;
            Frame(b, out _, out Vector3 fwd, out _);
            Quaternion frame = Quaternion.LookRotation(fwd, Vector3.up);
            var rb = it.Rb;
            Quaternion world;
            if (it.IsGun)
            {
                Quaternion rel = Quaternion.Inverse(rb.rotation) * Muzzle(it).rotation;
                world = Quaternion.LookRotation((fwd - Vector3.up * 0.35f).normalized, Vector3.up) * Quaternion.Inverse(rel);
            }
            else if (LongAxis(rb.transform, it.Colliders, it.Position, out Vector3 axis, out _, out float ratio) && ratio > 2f)
            {
                // Grip at the -axis end; the rest points forward and up.
                world = Quaternion.FromToRotation(axis, (fwd + Vector3.up).normalized) * rb.rotation;
            }
            else world = rb.rotation;
            return Quaternion.Inverse(frame) * world;
        }

        private static Quaternion CarryRotation(Hands h, FruitBody b)
        {
            Frame(b, out _, out Vector3 fwd, out _);
            return Quaternion.LookRotation(fwd, Vector3.up) * h.Carry;
        }

        private static Vector3 GripWorld(Hands h, FruitBody b)
        {
            var it = h.Item;
            if (it.Grip.HasValue) return it.Rb.position + it.Rb.rotation * it.Grip.Value;
            var hand = b.Limbs[h.Left ? LeftHand : RightHand];
            return ClosestOn(it.Colliders, hand.Present ? hand.LivePosition : b.Position + Vector3.up, it.Position);
        }

        // ── Collisions with the holder ──

        private static void IgnoreHolder(Hands h, bool ignore)
        {
            if (!ignore || h.Item.Colliders == null) return;
            foreach (var limb in h.Body.Limbs)
            {
                if (!limb.Present || limb.Physics == null) continue;
                BoxCollider[] own;
                try { own = limb.Physics.m_colliders; } catch { continue; }
                if (own == null) continue;
                foreach (var oc in own)
                {
                    if (oc == null) continue;
                    foreach (var ic in h.Item.Colliders)
                    {
                        if (ic == null || ic.WasCollected) continue;
                        try { Physics.IgnoreCollision(ic, oc, true); h.Ignored.Add((ic, oc)); } catch { }
                    }
                }
            }
        }

        private static void Unignore(bool all)
        {
            float now = Time.time;
            for (int i = _unignore.Count - 1; i >= 0; i--)
            {
                if (!all && now < _unignore[i].at) continue;
                foreach (var (a, c) in _unignore[i].pairs)
                    try { if (a != null && c != null && !a.WasCollected && !c.WasCollected) Physics.IgnoreCollision(a, c, false); } catch { }
                _unignore.RemoveAt(i);
            }
        }

        // ── Standing where the reach works (5.33.0) ──
        //
        // Walking up to an item used to end wherever the walk ended: 0.55 m from its middle at best,
        // 0.4 m or on top of it when the spot was taken or a door was near (the claim fell back to
        // the item's own point), and a stepping turn to face it drifted a body another 20 cm. A
        // crouch with the item between the feet puts the knees where the hand is going. So now a
        // body picks its stance first: PickupStance (or PickupStanceLow, for a crouch) from the
        // item's nearest point, on the side it comes in from, free of others and with nothing solid
        // between it and the item, and walks onto that (ending exactly there, with no turn to the
        // group). At the item, once it has turned to face it, the distance is checked again.

        /// <summary>How far from the item this hand action wants the pelvis (flat, to the item's
        /// nearest point): further for an item low enough to crouch to, whose knees come forward.</summary>
        private static float StanceFor(Hands h, FruitBody b)
        {
            bool low = CrouchToPickUp && h.Item.Position.y < LiveCentre(b).y - CrouchBelow;
            return Mathf.Clamp(low ? FruitLibConfig.PickupStanceLow : FruitLibConfig.PickupStance, 0.3f, 1.2f);
        }

        /// <summary>Flat metres from a point (a pelvis) to the nearest point of an item's colliders.</summary>
        private static float ItemDistance(FruitWorldItem it, Vector3 from, out Vector3 nearest)
        {
            nearest = ClosestOn(it.Colliders, from, it.Position);
            return FruitNav.Flat(nearest - from);
        }

        /// <summary>The point the hand is sent to for a reach: the target, or as far toward it from the
        /// shoulder as the arm goes (<see cref="ArmReach"/>).</summary>
        private static Vector3 Reachable(Hands h, FruitBody b, Vector3 target)
        {
            Frame(b, out Vector3 chest, out _, out Vector3 right);
            Vector3 shoulder = chest + Vector3.up * 0.15f + right * (h.Left ? -0.2f : 0.2f);
            Vector3 to = target - shoulder;
            float d = to.magnitude;
            return d > ArmReach && d > 1e-3f ? shoulder + to / d * ArmReach : target;
        }

        private static readonly float[] StanceBearings = { 0f, 25f, -25f, 50f, -50f, 80f, -80f, 115f, -115f, 150f, -150f, 180f };

        /// <summary>
        /// A place to stand to reach an item: where the route to it first comes within the stance
        /// distance, or failing that a point on rings round it (the body's own side first), on the
        /// navmesh, not taken, with a clear line to the item. False if there is none.
        /// </summary>
        private static bool FindStance(FruitBody b, FruitWorldItem it, float want, out Vector3 stance)
        {
            Vector3 me = LiveCentre(b);
            ItemDistance(it, me, out Vector3 pt);
            stance = me;

            // The way in: walking back along the route from the item, the first point that is
            // far enough off it. Free and in sight of the item, it is the side the body arrives on.
            if (FruitNav.Ready && FruitNav.FindPath(me, pt, _probe, 3f, false, false) && _probe.Status == FruitPathStatus.Complete)
            {
                int steps = Mathf.Min(200, Mathf.CeilToInt(_probe.Length / 0.1f));
                for (int i = 0; i <= steps; i++)
                {
                    Vector3 c = _probe.PointAt(_probe.Length - i * 0.1f);
                    if (FruitNav.Flat(c - pt) < want) continue;
                    if (StanceOk(b, it, c, pt, out Vector3 q)) { stance = q; return true; }
                    break;
                }
            }

            float own = MathF.Atan2(me.z - pt.z, me.x - pt.x);
            for (float r = want; r <= want + 1.2f; r += 0.2f)
            {
                foreach (float deg in StanceBearings)
                {
                    float a = own + deg * Mathf.Deg2Rad;
                    var c = new Vector3(pt.x + MathF.Cos(a) * r, me.y, pt.z + MathF.Sin(a) * r);
                    if (StanceOk(b, it, c, pt, out Vector3 q)) { stance = q; return true; }
                }
            }
            return false;
        }

        private static bool StanceOk(FruitBody b, FruitWorldItem it, Vector3 c, Vector3 pt, out Vector3 q)
        {
            q = c;
            // The floor under it (the point is at pelvis height): a table top or a wall is no floor.
            Vector3 ground = c;
            if (Physics.Raycast(new Vector3(c.x, c.y + 0.5f, c.z), Vector3.down, out RaycastHit fl, 3f, FruitNav.Layers, QueryTriggerInteraction.Ignore))
                ground = fl.point;
            if (FruitNav.Ready)
            {
                if (!FruitNav.Sample(ground, 0.5f, out q) || FruitNav.Flat(q - ground) > 0.3f) return false;
            }
            else q = ground;
            if (Taken(q, b)) return false;
            // Nothing solid between a chest standing there and the item (its own colliders aside).
            Vector3 from = q + Vector3.up * 0.9f, to = pt + Vector3.up * 0.15f, d = to - from;
            float dist = d.magnitude;
            if (dist > 0.05f && Physics.Raycast(from, d / dist, out RaycastHit hit, dist, FruitNav.Layers, QueryTriggerInteraction.Ignore))
            {
                var hitRb = hit.collider != null ? hit.collider.attachedRigidbody : null;
                if (hitRb != it.Rb && FruitNav.Flat(hit.point - pt) > 0.12f) return false;
            }
            return true;
        }

        /// <summary>Starts the walk to a stance for this hand action. False if no stance was found.</summary>
        private static bool WalkToStance(Hands h, FruitBody b, bool run, float seconds)
        {
            if (!FindStance(b, h.Item, StanceFor(h, b), out Vector3 s)) return false;
            Vector3 to = s - LiveCentre(b);
            to.y = 0f;
            float len = to.magnitude;
            // A goto order stops StanceStop short of its point; put the point that far beyond the
            // stance along the way in, so the body ends on the stance, not short of it.
            Vector3 dest = len > 0.05f ? s + to / len * StanceStop : s;
            h.Stance = s;
            h.ItemAt = h.Item.Position;
            FruitLog.Info($"[FruitHands] #{b.Id} walks to a stance {ItemDistance(h.Item, s, out _):0.00} m off {h.Item.Name} ({len:0.0} m away).");
            // Running a short way only ends further past the stance: it walks the last few metres.
            return GoTo(b, dest, StanceStop, seconds, run && len > 4f, false, false, s);
        }

        // ── Helpers ──

        /// <summary>A body's chest, its flat facing, and its right.</summary>
        private static void Frame(FruitBody b, out Vector3 chest, out Vector3 fwd, out Vector3 right)
        {
            var t = b.Limbs[FruitBodies.Torso];
            chest = t.Present ? t.LivePosition : LiveCentre(b) + Vector3.up * 0.35f;
            fwd = b.Facing; fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            right = Vector3.Cross(Vector3.up, fwd);
        }

        private static Hands Held(FruitBody b)
            => b != null && _hands.TryGetValue(b.Id, out var h) && h.Joint != null && h.Stage == HandStage.Hold ? h : null;

        /// <summary>The hand a body holds a swingable item in: 0 right, 1 left, -1 none (Strikes uses it).</summary>
        internal static int HeldHand(FruitBody b)
        {
            var h = b != null && _hands.TryGetValue(b.Id, out var x) && x.Joint != null ? x : null;
            return h == null ? -1 : h.Left ? 1 : 0;
        }

        private static bool HasHand(FruitBody b, bool? left = null)
        {
            if (left.HasValue) return b.Limbs[left.Value ? LeftHand : RightHand].Present;
            return b.Limbs[RightHand].Present || b.Limbs[LeftHand].Present;
        }

        private static Rigidbody HandRb(FruitBody b, bool left) => Rb(b.Limbs[left ? LeftHand : RightHand]);

        private static string Src(string s) => string.IsNullOrEmpty(s) ? "FruitLib" : s;

        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : fallback;
        }

        private static float Extent(Il2CppArrayBase<Collider> cols, Vector3 centre, Vector3 dir)
        {
            float best = 0f;
            if (cols == null) return best;
            foreach (var c in cols)
            {
                if (c == null || c.WasCollected || !c.enabled) continue;
                try { best = Mathf.Max(best, Vector3.Dot(c.ClosestPoint(centre + dir * 5f) - centre, dir)); } catch { }
            }
            return best;
        }

        private static bool LongAxis(Transform t, Il2CppArrayBase<Collider> cols, Vector3 centre, out Vector3 axis, out float half, out float ratio)
        {
            Vector3[] axes = { t.right, t.up, t.forward };
            float[] len = new float[3];
            for (int i = 0; i < 3; i++) len[i] = Extent(cols, centre, axes[i]) + Extent(cols, centre, -axes[i]);
            int a = 0;
            for (int i = 1; i < 3; i++) if (len[i] > len[a]) a = i;
            float second = 0f;
            for (int i = 0; i < 3; i++) if (i != a) second = Mathf.Max(second, len[i]);
            axis = axes[a];
            half = len[a] * 0.5f;
            ratio = second > 1e-3f ? len[a] / second : 1f;
            return len[a] > 0.05f;
        }

        private static Vector3 ClosestOn(Il2CppArrayBase<Collider> cols, Vector3 from, Vector3 fallback)
        {
            float best = float.MaxValue;
            Vector3 point = fallback;
            if (cols == null) return point;
            foreach (var c in cols)
            {
                if (c == null || c.WasCollected || !c.enabled) continue;
                Vector3 p;
                try { p = c.ClosestPoint(from); } catch { continue; }
                float d = (p - from).sqrMagnitude;
                if (d < best) { best = d; point = p; }
            }
            return point;
        }

        // ── Handling recipes (5.30.0) ──

        private sealed class Recipe
        {
            public FruitBody Body;
            public FruitWorldItem Item;
            public FruitUseStep[] Steps;
            public int Index = -1;
            public FruitBody Target;
            public Vector3 Point;
            public bool HasPoint, Run, Started;
            public string Source = "FruitLib";
            public float StepAt, NextAt;
            public Vector3 ItemAt;
        }

        private static readonly Dictionary<int, Recipe> _recipes = new Dictionary<int, Recipe>();
        private static readonly List<Recipe> _recipesNow = new List<Recipe>();

        /// <summary>Whether a body is working through an item's handling recipe. Since 5.30.0.</summary>
        public static bool IsHandling(FruitBody body) => body != null && _recipes.ContainsKey(body.Id);

        private static bool StartRecipe(FruitBody body, Hands h, FruitWorldItem it, FruitBody target, Vector3? point, bool run)
        {
            if (FruitHandling.NeedsTarget(it.Handling) && target == null && !point.HasValue)
                point = body.LiveHeadPosition + body.Facing * 8f;   // nobody named: ahead, away from itself
            var r = new Recipe
            {
                Body = body, Item = it, Steps = it.Handling, Target = target, Run = run, Source = h.Source,
                HasPoint = point.HasValue, Point = point ?? Vector3.zero,
            };
            _recipes[body.Id] = r;
            Next(r);
            return true;
        }

        private static Vector3 RecipeTarget(Recipe r)
        {
            var t = r.Target;
            if (t != null && t.HasPosition)
            {
                var torso = t.Limbs[FruitBodies.Torso];
                return torso.Present ? torso.LivePosition : t.LiveHeadPosition;
            }
            return r.HasPoint ? r.Point : r.Body.LiveHeadPosition + r.Body.Facing * 8f;
        }

        private static void TickRecipes()
        {
            _recipesNow.Clear();
            _recipesNow.AddRange(_recipes.Values);
            float now = Time.time;
            foreach (var r in _recipesNow)
            {
                if (!_recipes.TryGetValue(r.Body.Id, out var cur) || cur != r) continue;
                try { TickRecipe(r, now); }
                catch (Exception e) { FruitBodies.ReportOnce("item handling", e); Finish(r, FruitHandsResult.Failed); }
            }
        }

        private static void TickRecipe(Recipe r, float now)
        {
            var b = r.Body;
            var it = r.Item;
            if (b.Creature == null || FruitBodies.Get(b.Id) != b || !it.Valid) { Finish(r, FruitHandsResult.Lost); return; }
            if (now < r.NextAt) return;
            var step = r.Steps[r.Index];
            _hands.TryGetValue(b.Id, out var h);
            bool holding = h != null && h.Item == it && h.Joint != null;
            float t = now - r.StepAt;

            switch (step)
            {
                case FruitUseStep.Aim:
                    if (!holding) { Finish(r, FruitHandsResult.Lost); return; }
                    if (h.Aimed) Next(r);
                    else if (h.Stage != HandStage.Aim && t > 0.2f) Finish(r, FruitHandsResult.Failed);   // fell, or lost its footing
                    return;

                case FruitUseStep.Throw:
                    // Done once it has left the hand.
                    if (!holding) { Next(r); return; }
                    if (h.Stage == HandStage.Hold && t > 0.2f) Finish(r, FruitHandsResult.Failed);   // the throw was cut short
                    return;

                case FruitUseStep.Retreat:
                    if (FruitNav.Flat(LiveCentre(b) - r.ItemAt) >= it.RetreatDistance || (t > 0.5f && !IsWalking(b)) || t > 12f)
                    {
                        if (IsWalking(b)) Stop(b);
                        Next(r);
                    }
                    return;

                default:   // Use, PutDown and Wait finish when they start; NextAt held the pause.
                    Next(r);
                    return;
            }
        }

        /// <summary>Starts the next step (or ends the recipe after the last).</summary>
        private static void Next(Recipe r)
        {
            var b = r.Body;
            var it = r.Item;
            r.Index++;
            r.StepAt = Time.time;
            r.NextAt = 0f;
            if (r.Index >= r.Steps.Length) { Finish(r, FruitHandsResult.Used); return; }
            _hands.TryGetValue(b.Id, out var h);
            bool holding = h != null && h.Item == it && h.Joint != null;

            switch (r.Steps[r.Index])
            {
                case FruitUseStep.Aim:
                    if (!holding || !Upright(b)) { Finish(r, FruitHandsResult.Failed); return; }
                    h.Target = r.Target;
                    h.Point = RecipeTarget(r);
                    h.HasPoint = true;
                    h.AimOnly = true;
                    h.Aimed = false;
                    SetStage(h, HandStage.Aim);
                    return;

                case FruitUseStep.Use:
                {
                    bool ok = false;
                    Vector3? at = r.Target == null ? RecipeTarget(r) : (Vector3?)null;
                    try { ok = it.OnUse != null && it.OnUse(b, it, r.Target, at); }
                    catch (Exception e) { FruitBodies.ReportOnce("item OnUse", e); }
                    if (!ok) { Finish(r, FruitHandsResult.Failed); return; }
                    r.NextAt = Time.time + Mathf.Max(0f, it.HandlingPause);
                    return;
                }

                case FruitUseStep.Throw:
                    if (!holding) { Finish(r, FruitHandsResult.Lost); return; }
                    if (h.Stage != HandStage.Hold) { h.AimOnly = false; SetStage(h, HandStage.Hold); }
                    if (!ThrowNow(b, h, r.Target != null ? r.Target.LiveHeadPosition : RecipeTarget(r), 1f, r.Source))
                        Finish(r, FruitHandsResult.Failed);
                    return;

                case FruitUseStep.PutDown:
                    if (!holding) { Finish(r, FruitHandsResult.Lost); return; }
                    PutDown(h, b, RecipeTarget(r));
                    r.NextAt = Time.time + 0.4f;   // let it settle before anything is done to it
                    return;

                case FruitUseStep.Retreat:
                    r.ItemAt = it.Position;
                    if (FruitNav.Flat(LiveCentre(b) - r.ItemAt) >= it.RetreatDistance) { Next(r); return; }
                    WalkAway(b, r.ItemAt, 12f, true);
                    return;

                case FruitUseStep.Wait:
                    r.NextAt = Time.time + Mathf.Max(0f, it.HandlingPause);
                    return;
            }
        }

        private static void Finish(Recipe r, FruitHandsResult result)
        {
            if (_recipes.TryGetValue(r.Body.Id, out var cur) && cur == r) _recipes.Remove(r.Body.Id);
            if (_hands.TryGetValue(r.Body.Id, out var h) && h.Item == r.Item && h.AimOnly)
            {
                h.AimOnly = false;
                h.Aimed = false;
                if (h.Joint != null && h.Stage == HandStage.Aim) SetStage(h, HandStage.Hold);
            }
            HandsEnd(r.Body, r.Item, result);
        }

        /// <summary>Sets what the body holds down on the ground in front of it, upright, facing the target.</summary>
        private static void PutDown(Hands h, FruitBody b, Vector3 facing)
        {
            var it = h.Item;
            Frame(b, out Vector3 chest, out Vector3 fwd, out _);
            Vector3 toward = Flat(facing - LiveCentre(b), fwd);
            Vector3 spot = LiveCentre(b) + toward * 0.6f;
            float halfUp = 0.05f;
            try { halfUp = Mathf.Max(0.02f, it.Size.y * 0.5f); } catch { }
            Vector3 ground = spot;
            if (Physics.Raycast(spot + Vector3.up * 1.2f, Vector3.down, out RaycastHit hit, 3f, FruitNav.Layers, QueryTriggerInteraction.Ignore))
                ground = hit.point;
            Quaternion rot = Quaternion.LookRotation(toward, Vector3.up) * it.PutDownRotation;
            string src = h.Source;
            Detach(h, FruitHandsResult.Dropped, publish: false);
            if (!it.Valid) return;
            var rb = it.Rb;
            Vector3 pos = ground + Vector3.up * (halfUp + 0.02f);
            rb.transform.SetPositionAndRotation(pos, rot);
            rb.position = pos;
            rb.rotation = rot;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            FruitEvents.Publish(src, "PutDown", $"{b.Name} set down {it.WithArticle}", pos, radius: 10f, body: b.Id,
                                magnitude: it.Can(FruitAffords.Explosive) ? 0.6f : 0.2f,
                                actor: FruitEvents.BodyActor(b.Id), cause: it.Name);
        }

        private static void ResetHands()
        {
            _recipes.Clear();
            foreach (var h in _hands.Values)
            {
                try { if (h.Joint != null && !h.Joint.WasCollected) Object.DestroyImmediate(h.Joint); } catch { }
                try { if (h.Item.Valid && h.MassCapped) h.Item.Rb.mass = h.Item.Mass; } catch { }
            }
            _hands.Clear();
            _unignore.Clear();
        }
    }
}
