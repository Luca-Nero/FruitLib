using System;
using System.Collections.Generic;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        private static readonly Dictionary<int, FruitInteraction> _interactions = new Dictionary<int, FruitInteraction>();
        private static readonly List<FruitInteraction> _interactionTick = new List<FruitInteraction>();
        private static int _interactionCommand;
        private static int _interactionSearches;
        private static FruitInteraction _endingInteraction;
        public static string InteractionResultDetail(FruitBody body, FruitWorldItem item)
            => _endingInteraction != null && _endingInteraction.Actor == body && _endingInteraction.Spec.Item == item ?
                _endingInteraction.Id + ": " + _endingInteraction.Detail : "";
        public static string InteractionResultDetail(FruitBody body)
            => _endingInteraction != null && _endingInteraction.Actor == body ?
                _endingInteraction.Id + ": " + _endingInteraction.Detail : "";
        public static event Action<FruitInteraction> InteractionEnded;
        public static FruitInteraction InteractionOf(FruitBody body)
            => body != null && _interactions.TryGetValue(body.Id, out var op) ? op : null;

        /// <summary>Shared stance query for pickup and other controllers that own their own action lifecycle.</summary>
        public static bool TryInteractionStance(FruitBody actor, FruitInteractionSpec spec, out Vector3 stance, out FruitPoseShape pose)
        {
            stance = default; pose = null;
            if (actor == null || !FruitInteractionPolicy.ValidGeometry(spec)) return false;
            Vector3? point = spec.ResolvePoint();
            if (!point.HasValue || !FruitInteractionPolicy.PointFinite(point.Value)) return false;
            return FindInteractionStance(new FruitInteraction { Actor = actor, Spec = spec, Point = point.Value, Attempts = 1 }, out stance, out pose);
        }

        private static bool StartItemInteraction(FruitBody body, FruitWorldItem item, FruitBody target, Vector3? point,
                                                Action<FruitInteraction> ended)
        {
            try
            {
                var spec = item.Interaction(body, item, target, point);
                if (spec == null) return false;
                spec.Item = item;
                if (spec.ToolReach == 0f) spec.ToolReach = item.ToolReach;
                spec.Target = target ?? spec.Target;
                return Interact(body, spec, ended);
            }
            catch (Exception e) { FruitBodies.ReportOnce("item interaction builder", e); return false; }
        }

        /// <summary>Detach an item consumed by the currently applying interaction without emitting a
        /// spurious Dropped interruption. The item owner deletes it; the token reports the outcome.</summary>
        public static bool ConsumeInteractionItem(FruitInteraction op)
        {
            if (op == null || op.Stage != FruitInteractionStage.Applying || InteractionOf(op.Actor) != op ||
                (!op.Committing && !CanApplyInteraction(op)) ||
                op.Spec.Item == null || !_hands.TryGetValue(op.Actor.Id, out var h) || h.Item != op.Spec.Item) return false;
            Detach(h, FruitHandsResult.Used, publish: false, notify: false);
            op.Consumed = true;
            return true;
        }

        /// <summary>Recheck immediately before changing the world in a delayed callback.
        /// A successful approach, earlier contact or acceptance is never evidence of current contact.</summary>
        public static bool CanApplyInteraction(FruitInteraction op)
        {
            if (op == null || op.Stage != FruitInteractionStage.Applying || InteractionOf(op.Actor) != op) return false;
            var b = op.Actor; var s = op.Spec;
            if (Time.time >= op.Deadline || FruitBodies.Get(b.Id) != b || !b.Alive || !b.Conscious || !Upright(b) ||
                (s.Target != null && (FruitBodies.Get(s.Target.Id) != s.Target || (!s.Target.Alive && !s.AllowTargetDeathAfterApply))) ||
                (!op.Consumed && s.Item != null && (!s.Item.Valid || (s.RequireHeldItem && Holding(b) != s.Item))) ||
                (s.Valid != null && !s.Valid())) return false;
            Vector3? point = s.ResolvePoint();
            if (!point.HasValue || !FruitInteractionPolicy.PointFinite(point.Value)) return false;
            op.Point = point.Value;
            return !s.RequireContact || InteractionContact(op);
        }

        /// <summary>Starts a general interaction, independent of inventory and NeuroFrukt.
        /// A callback is delivered exactly once. A true return means accepted, never completed.</summary>
        public static bool Interact(FruitBody actor, FruitInteractionSpec spec, Action<FruitInteraction> ended = null)
        {
            if (actor == null || !actor.Alive || !actor.Conscious || !Upright(actor) ||
                !FruitInteractionPolicy.Valid(spec) || IsReaching(actor) || Swinging(actor) ||
                (FruitPose.IsHolding(actor) && !FruitPose.IsReleasing(actor)) || NativeCareBusy(actor)) return false;
            if (InteractionOf(actor) != null) return false; // Retain the original identity and deadline.
            bool left = spec.Item != null && HeldHand(actor) >= 0 ? HeldHand(actor) == 1 : spec.Left;
            if (!HasHand(actor, left)) return false;
            spec.Left = left;
            Stop(actor); // Finish the previous movement before registering the new owner.
            var op = new FruitInteraction { Actor = actor, Spec = spec, Id = FruitInteractionTrace.NewId(spec.Name),
                Started = Time.time, Deadline = Time.time + spec.Seconds, StageAt = Time.time, OnEnded = ended };
            _interactions[actor.Id] = op;
            ChangeInteraction(op, FruitInteractionStage.Accepted, "target=#" + (spec.Target?.Id ?? 0) + " item=" + (spec.Item?.Key ?? "none"));
            return true;
        }

        public static void CancelInteraction(FruitBody actor, string reason = "a new order replaced it")
        {
            var op = InteractionOf(actor);
            if (op != null) FinishInteraction(op, FruitInteractionStage.Cancelled, reason);
        }

        internal static void EndInteraction(FruitInteraction op, bool success, string detail)
        {
            if (op == null || op.Terminal || InteractionOf(op.Actor) != op || !op.Began) return;
            if (success && !op.Committed && !CanApplyInteraction(op)) { success = false; detail = "completion lost its working prerequisites or contact"; }
            FinishInteraction(op, success ? FruitInteractionStage.Completed : FruitInteractionStage.Failed, detail);
        }

        internal static bool ApplyInteraction(FruitInteraction op, Func<bool> change, string detail)
        {
            if (op?.Committing == true) return false;
            if (change == null || !CanApplyInteraction(op))
            {
                if (op != null && !op.Terminal && InteractionOf(op.Actor) == op)
                    FinishInteraction(op, FruitInteractionStage.Failed, "application lost its working prerequisites or contact");
                return false;
            }
            bool changed;
            op.Committing = true;
            try { changed = change(); }
            catch (Exception e) { FruitBodies.ReportOnce("interaction apply", e); op.Fail("application threw"); return false; }
            finally { op.Committing = false; }
            // A synchronous world callback may cancel this attempt. Never finish its replacement.
            if (InteractionOf(op.Actor) != op || op.Terminal) return false;
            op.Committed = changed;
            if (changed) op.Complete(detail); else op.Fail("application rejected: " + detail);
            return changed;
        }

        private static void InteractionCommand(Action action)
        {
            _interactionCommand++;
            try { action(); } finally { _interactionCommand--; }
        }

        private static void ChangeInteraction(FruitInteraction op, FruitInteractionStage stage, string detail = "")
        {
            op.Stage = stage; op.StageAt = Time.time; op.Detail = detail;
            op.NextTrace = Time.time + 1f;
            TraceInteraction(op);
        }

        private static void TraceInteraction(FruitInteraction op)
            => FruitInteractionTrace.Write(op.Id, op.Stage.ToString(), op.Actor, op.Point,
                op.Spec.Reach + op.Spec.ToolReach, op.Spec.Left,
                "attempt=" + op.Attempts + " age=" + (Time.time - op.Started).ToString("F2") + " " + op.Detail, op.Stance);

        private static void TickInteractions()
        {
            _interactionSearches = 1; // Keep simultaneous new interactions from bursting nav queries.
            _interactionTick.Clear(); _interactionTick.AddRange(_interactions.Values);
            foreach (var op in _interactionTick)
            {
                if (InteractionOf(op.Actor) != op) continue;
                try { TickInteraction(op, Time.time); }
                catch (Exception e)
                {
                    FruitBodies.ReportOnce("interaction", e);
                    FinishInteraction(op, FruitInteractionStage.Failed, "interaction callback threw");
                }
            }
        }

        private static void TickInteraction(FruitInteraction op, float now)
        {
            var b = op.Actor; var s = op.Spec;
            if (FruitBodies.Get(b.Id) != b || !b.Alive || !b.Conscious || !Upright(b))
            { FinishInteraction(op, FruitInteractionStage.Failed, "actor unavailable or lost footing"); return; }
            if ((s.Target != null && (FruitBodies.Get(s.Target.Id) != s.Target ||
                    (!s.Target.Alive && !(op.Began && s.AllowTargetDeathAfterApply)))) ||
                (s.Item != null && (!s.Item.Valid || (s.RequireHeldItem && Holding(b) != s.Item))) ||
                (s.Valid != null && !s.Valid()))
            { FinishInteraction(op, FruitInteractionStage.Failed, "target, tool or prerequisites unavailable"); return; }
            if (now >= op.Deadline) { FinishInteraction(op, FruitInteractionStage.Failed, "interaction deadline expired"); return; }
            Vector3? point = s.ResolvePoint();
            if (!point.HasValue || !FruitInteractionPolicy.PointFinite(point.Value))
            { FinishInteraction(op, FruitInteractionStage.Failed, "working point unavailable"); return; }
            Vector3 previous = op.Point; op.Point = point.Value;
            if (now >= op.NextTrace) { op.NextTrace = now + 1f; TraceInteraction(op); }
            if (op.Stage == FruitInteractionStage.Applying)
            {
                if (s.RequireContact && now >= op.NextHandAt)
                {
                    op.NextHandAt = now + 0.25f;
                    Vector3 shoulder = InteractionShoulder(b, s.Left);
                    op.Placed = PlaceHand(b, s.Left, op.Point - (op.Point - shoulder).normalized * s.ToolReach, 1.6f);
                }
                // Continue enforcing contact while a delayed tool or machine operation is active.
                if (s.RequireContact && !InteractionContact(op))
                { FinishInteraction(op, FruitInteractionStage.Failed, "lost working contact while applying"); }
                return;
            }
            if (s.Target != b && op.Stage != FruitInteractionStage.Accepted && Vector3.Distance(previous, op.Point) > 0.35f)
            { RepositionInteraction(op, "working point moved"); return; }

            switch (op.Stage)
            {
                case FruitInteractionStage.Accepted:
                    PositionInteraction(op);
                    break;
                case FruitInteractionStage.Positioning:
                    if (IsWalking(b)) return;
                    ChangeInteraction(op, FruitInteractionStage.Facing);
                    InteractionCommand(() => Face(b, op.Point - LiveCentre(b), 1.5f));
                    break;
                case FruitInteractionStage.Facing:
                    if (IsTurning(b) && now - op.StageAt < 1.6f) return;
                    InteractionCommand(() => Stop(b));
                    if (op.Stance.HasValue && FruitNav.Flat(op.Stance.Value - LiveCentre(b)) > 0.25f)
                    { RepositionInteraction(op, "turn drifted outside working stance"); return; }
                    if (op.Pose != null && !FruitPose.Hold(b, op.Pose, 0.5f, source: s.Source))
                    { FinishInteraction(op, FruitInteractionStage.Failed, "working posture unavailable"); return; }
                    ChangeInteraction(op, FruitInteractionStage.Posing);
                    break;
                case FruitInteractionStage.Posing:
                    if (op.Pose != null && FruitPose.Current(b) != op.Pose)
                    { FinishInteraction(op, FruitInteractionStage.Failed, "working posture interrupted"); return; }
                    if (now - op.StageAt < 0.6f || (op.Pose != null && FruitPose.Weight(b) < 0.9f)) return;
                    if (Vector3.Distance(InteractionShoulder(b, s.Left), op.Point) > s.Reach + s.ToolReach + 0.05f)
                    { RepositionInteraction(op, "working point beyond arm/tool reach"); return; }
                    ChangeInteraction(op, FruitInteractionStage.Reaching);
                    break;
                case FruitInteractionStage.Reaching:
                    if (s.RequireContact && now >= op.NextHandAt)
                    {
                        op.NextHandAt = now + 0.25f;
                        Vector3 shoulder = InteractionShoulder(b, s.Left);
                        Vector3 direction = (op.Point - shoulder).normalized;
                        op.Placed = PlaceHand(b, s.Left, op.Point - direction * s.ToolReach, 1.6f);
                    }
                    bool contact = !s.RequireContact || InteractionContact(op);
                    op.ContactAt = FruitInteractionPolicy.ContactStart(contact, op.ContactAt, now);
                    if (FruitInteractionPolicy.Sustained(op.ContactAt, now, s.ContactSeconds))
                    {
                        op.Began = true; ChangeInteraction(op, FruitInteractionStage.Applying);
                        s.Begin(op); // Deferred implementations retain this identity-bearing token.
                    }
                    else if (now - op.StageAt > 3f) RepositionInteraction(op, "hand could not establish contact");
                    break;
            }
        }

        private static Vector3 InteractionShoulder(FruitBody b, bool left)
        {
            Frame(b, out Vector3 chest, out _, out Vector3 right);
            return chest + Vector3.up * 0.15f + right * (left ? -0.2f : 0.2f);
        }

        private static bool InteractionContact(FruitInteraction op)
        {
            var hand = op.Actor.Limbs[op.Spec.Left ? LeftHand : RightHand];
            if (!hand.Present || !op.Placed) return false;
            Vector3 shoulder = InteractionShoulder(op.Actor, op.Spec.Left);
            Vector3 desired = op.Point - (op.Point - shoulder).normalized * op.Spec.ToolReach;
            bool settled = op.Pose == null || (FruitPose.Current(op.Actor) == op.Pose && FruitPose.Weight(op.Actor) >= 0.9f);
            if (!FruitInteractionPolicy.Contact(Vector3.Distance(hand.LivePosition, desired), op.Spec.ContactTolerance, settled)) return false;
            var tipResolver = op.Spec.Item?.WorkingPoint;
            if (tipResolver == null) return true;
            Vector3? tip = tipResolver();
            return tip.HasValue && FruitInteractionPolicy.PointFinite(tip.Value) &&
                Vector3.Distance(tip.Value, op.Point) <= op.Spec.ContactTolerance;
        }

        private static void RepositionInteraction(FruitInteraction op, string why)
        {
            if (op.Placed) { ReleaseHand(op.Actor, op.Spec.Left); op.Placed = false; }
            if (op.Pose != null && FruitPose.Current(op.Actor) == op.Pose) FruitPose.Release(op.Actor, 0.25f);
            op.ContactAt = -1f;
            ChangeInteraction(op, FruitInteractionStage.Accepted, why);
            op.RetryAt = Time.time + 0.9f; // Give legs time to recover before another movement order.
        }

        private static void PositionInteraction(FruitInteraction op)
        {
            if (Time.time < op.RetryAt) return;
            if (FruitPose.IsReleasing(op.Actor)) return;
            if (op.Spec.Target != op.Actor && _interactionSearches-- <= 0) return;
            if (++op.Attempts > 4) { FinishInteraction(op, FruitInteractionStage.Failed, "no reachable working stance after four attempts"); return; }
            if (op.Spec.Target == op.Actor)
            {
                op.Stance = LiveCentre(op.Actor); op.Pose = null;
                float belowShoulder = InteractionShoulder(op.Actor, op.Spec.Left).y - op.Point.y;
                var allowed = op.Spec.Postures;
                if (belowShoulder > 0.7f && (allowed & FruitInteractionPostures.Kneel) != 0) op.Pose = FruitPoseShape.Kneel;
                else if (belowShoulder > 0.7f && (allowed & FruitInteractionPostures.Crouch) != 0) op.Pose = FruitPoseShape.Crouch;
                else if (belowShoulder > 0.4f && (allowed & FruitInteractionPostures.Lean) != 0) op.Pose = FruitPoseShape.Lean;
                if (op.Pose == null && (allowed & FruitInteractionPostures.Stand) == 0)
                    foreach (var posture in WorkingPostures)
                        if ((allowed & posture) != 0) { op.Pose = WorkingPose(posture); break; }
                if (op.Pose != null && !FruitPose.Hold(op.Actor, op.Pose, 0.5f, source: op.Spec.Source))
                { FinishInteraction(op, FruitInteractionStage.Failed, "self working posture unavailable"); return; }
                ChangeInteraction(op, FruitInteractionStage.Posing, "self interaction");
                return;
            }
            if (!FindInteractionStance(op, out Vector3 stance, out FruitPoseShape pose))
            { FinishInteraction(op, FruitInteractionStage.Failed, "no clear working stance within reach"); return; }
            op.Stance = stance; op.Pose = pose;
            ChangeInteraction(op, FruitInteractionStage.Positioning, "posture=" + (pose?.Name ?? "stand"));
            Vector3 delta = stance - LiveCentre(op.Actor);
            if (FruitNav.Flat(delta) <= 0.18f) return;
            InteractionCommand(() =>
            {
                if (FruitNav.Flat(delta) <= 2f && StepTo(op.Actor, stance, 5f)) return;
                Vector3 beyond = stance + FlatDirection(delta) * 0.3f;
                GoTo(op.Actor, beyond, 0.3f, Math.Min(15f, op.Deadline - Time.time),
                    op.Spec.Run && FruitNav.Flat(delta) > 4f, false, false, stance);
            });
        }

        private static readonly FruitInteractionPostures[] WorkingPostures = {
            FruitInteractionPostures.Stand, FruitInteractionPostures.Lean, FruitInteractionPostures.Kneel,
            FruitInteractionPostures.Crouch, FruitInteractionPostures.Sit };

        private static FruitPoseShape WorkingPose(FruitInteractionPostures posture)
        {
            switch (posture)
            {
                case FruitInteractionPostures.Lean: return FruitPoseShape.Lean;
                case FruitInteractionPostures.Kneel: return FruitPoseShape.Kneel;
                case FruitInteractionPostures.Crouch: return FruitPoseShape.Crouch;
                case FruitInteractionPostures.Sit: return FruitPoseShape.Sit;
                default: return null;
            }
        }

        // Candidates are around the live working point, not a target's pelvis or a social slot.
        // Score routes/reach/posture together. Every candidate needs a clear same-floor route,
        // free body space and an unobstructed arm segment. Actual contact is checked after posing.
        private static bool FindInteractionStance(FruitInteraction op, out Vector3 stance, out FruitPoseShape pose)
        {
            var b = op.Actor; var s = op.Spec;
            Vector3 me = LiveCentre(b), shoulder = InteractionShoulder(b, s.Left);
            stance = me; pose = null;
            if (!FruitNav.Under(me, 0.7f, out Vector3 floor)) return false;
            float best = float.PositiveInfinity;
            float bearing = MathF.Atan2(me.z - op.Point.z, me.x - op.Point.x);
            foreach (var posture in WorkingPostures)
            {
                if ((s.Postures & posture) == 0) continue;
                var shape = WorkingPose(posture);
                float lowering = posture == FruitInteractionPostures.Stand ? 0f : posture == FruitInteractionPostures.Lean ? 0.12f :
                    posture == FruitInteractionPostures.Kneel ? 0.45f : posture == FruitInteractionPostures.Crouch ? 0.5f : 0.55f;
                for (int ring = 0; ring < 4; ring++)
                foreach (float deg in StanceBearings)
                {
                    float radius = Math.Max(0.3f, s.Reach * 0.45f) + ring * 0.15f + s.ToolReach;
                    float angle = bearing + deg * Mathf.Deg2Rad + (op.Attempts > 1 ? (op.Attempts - 1) * 25f * Mathf.Deg2Rad : 0f);
                    Vector3 candidate = new Vector3(op.Point.x + MathF.Cos(angle) * radius, floor.y, op.Point.z + MathF.Sin(angle) * radius);
                    if (!FruitNav.Sample(candidate, 0.3f, out Vector3 q) || FruitNav.Flat(q - candidate) > 0.15f ||
                        Math.Abs(q.y - floor.y) > 0.25f || Taken(q, b, s.Target)) continue;
                    if (s.Target != null)
                    {
                        bool overlaps = false;
                        foreach (var limb in s.Target.Limbs)
                            if (limb.Present && FruitNav.Flat(limb.LivePosition - q) < 0.22f) { overlaps = true; break; }
                        if (overlaps) continue;
                    }
                    Vector3 facing = FlatDirection(op.Point - q);
                    Vector3 side = new Vector3(facing.z, 0f, -facing.x);
                    Vector3 predicted = q + Vector3.up * (shoulder.y - floor.y - lowering) + side * (s.Left ? -0.2f : 0.2f);
                    float reach = Vector3.Distance(predicted, op.Point);
                    if (reach > s.Reach + s.ToolReach || !InteractionLineClear(predicted, op.Point, s.Item)) continue;
                    float penalty = lowering * 0.7f + Math.Abs(deg) * 0.002f + reach * 0.2f;
                    bool sameFailedStance = op.Attempts > 1 && op.Stance.HasValue &&
                        FruitNav.Flat(q - op.Stance.Value) < 0.2f && shape == op.Pose;
                    if (sameFailedStance) penalty += 5f;
                    // A route cannot be shorter than the straight segment. Avoid path queries
                    // for candidates that cannot improve the current valid route's score.
                    if (FruitNav.Flat(q - me) + penalty >= best) continue;
                    if (!FruitNav.FindPath(me, q, _probe, 0.5f, false, false) || _probe.Status != FruitPathStatus.Complete) continue;
                    // A different approach/posture gets a chance after contact failed.
                    float score = _probe.Length + penalty;
                    if (score >= best) continue;
                    best = score; stance = q; pose = shape;
                }
            }
            return !float.IsPositiveInfinity(best);
        }

        private static bool InteractionLineClear(Vector3 from, Vector3 to, FruitWorldItem tool)
        {
            Vector3 delta = to - from; float distance = delta.magnitude;
            if (distance < 0.01f) return true;
            if (!Physics.Raycast(from, delta / distance, out RaycastHit hit, distance, FruitNav.Layers, QueryTriggerInteraction.Ignore)) return true;
            return (tool != null && hit.collider?.attachedRigidbody == tool.Rb) || Vector3.Distance(hit.point, to) <= 0.08f;
        }

        private static void FinishInteraction(FruitInteraction op, FruitInteractionStage result, string detail)
        {
            if (op.Terminal || InteractionOf(op.Actor) != op) return;
            _interactions.Remove(op.Actor.Id); // Before cleanup or callbacks: never mutate a replacement.
            ChangeInteraction(op, result, detail);
            InteractionCommand(() => Stop(op.Actor));
            if (op.Placed) ReleaseHand(op.Actor, op.Spec.Left);
            if (op.Pose != null && FruitPose.Current(op.Actor) == op.Pose) FruitPose.Release(op.Actor, 0.4f);
            if (op.Spec.Item != null && !op.Spec.Item.Valid &&
                _hands.TryGetValue(op.Actor.Id, out var hands) && hands.Item == op.Spec.Item)
                Detach(hands, FruitHandsResult.Used, publish: false, notify: false);
            if (result != FruitInteractionStage.Completed)
                try { op.Spec.Cancel?.Invoke(); } catch (Exception e) { FruitBodies.ReportOnce("interaction cancel callback", e); }
            var previousEnding = _endingInteraction;
            _endingInteraction = op;
            try { op.OnEnded?.Invoke(op); } catch (Exception e) { FruitBodies.ReportOnce("interaction end callback", e); }
            finally { _endingInteraction = previousEnding; }
            try { InteractionEnded?.Invoke(op); } catch (Exception e) { FruitBodies.ReportOnce("interaction event", e); }
        }

        private static void ResetInteractions()
        {
            var pending = new List<FruitInteraction>(_interactions.Values);
            foreach (var op in pending) FinishInteraction(op, FruitInteractionStage.Cancelled, "scene reset");
            _interactions.Clear(); _interactionTick.Clear();
        }
    }
}
