using System;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        private static readonly float[] LocalStepHeights = { -0.45f, 0f, 0.4f };
        /// <summary>A short step to a nearby point, keeping the body's current facing.
        /// Uses the native sideways/backward gait. Requires a clear, level navmesh leg,
        /// at most 3 metres long. Reports Arrived or Blocked through MoveEnded. Since 5.38.0.</summary>
        public static bool StepTo(FruitBody body, Vector3 point, float seconds = 5f)
        {
            if (body == null || !body.Alive || !body.Conscious || !Upright(body) ||
                !LocalLegClear(body, LiveCentre(body), point)) return false;
            var o = OrderFor(body, seconds, false);
            o.HasDest = true; o.Stance = true; o.Dest = o.Stand = o.Want = point;
            o.StopDistance = o.WantStop = 0.18f;
            if (!Bind(o) || !BeginLocalStep(o, point, Time.time))
            { Stop(body); return false; }
            o.LocalStandalone = true;
            return true;
        }

        private static bool BeginLocalStep(Order o, Vector3 point, float now)
        {
            Vector3 me = LiveCentre(o.Body);
            if (!LocalLegClear(o.Body, me, point)) return false;
            o.LocalStep = true; o.LocalGoal = point;
            o.LocalYaw = o.Runtime.CreatureYRotation;
            o.LocalLeft = FruitNav.Flat(point - me);
            o.LocalDeadline = Math.Min(o.Until, now + Math.Clamp(o.LocalLeft / 0.3f + 2f, 3f, 10f));
            o.LocalProgressAt = now + 2f;
            o.LocalSettleAt = 0f;
            o.LocalCheckAt = now;
            o.Dir = FlatDirection(point - me);
            o.Pivot = false; o.StepTurning = false; o.TrafficBlocked = false;
            o.YieldUntil = 0f; o.YawCmdOn = false;
            return true;
        }

        private static Vector3 FlatDirection(Vector3 v)
        { v.y = 0f; return v.normalized; }

        // Check the whole segment, including floor samples between its endpoints. A floor
        // found on another storey, beyond a ledge or across a hole cannot authorize a step.
        private static bool LocalLegClear(FruitBody body, Vector3 from, Vector3 to)
        {
            float length = FruitNav.Flat(to - from);
            if (!FruitNav.Ready || length < 0.05f || length > 3f ||
                !FruitNav.Under(from, 0.5f, out Vector3 start) ||
                FruitNav.Flat(start - from) > 0.15f ||
                !FruitNav.Under(to, 0.5f, out Vector3 end) || FruitNav.Flat(end - to) > 0.15f ||
                Math.Abs(end.y - start.y) > 0.25f || !PassLegClear(body, from, to)) return false;
            int samples = Math.Max(1, (int)Math.Ceiling(length / 0.2f));
            for (int i = 1; i <= samples; i++)
            {
                Vector3 p = from + (to - from) * ((float)i / samples);
                p.y = from.y;
                if (!FruitNav.Under(p, 0.5f, out Vector3 floor) ||
                    FruitNav.Flat(floor - p) > 0.15f || Math.Abs(floor.y - start.y) > 0.25f) return false;
            }
            Vector3 dir = FlatDirection(to - from);
            // Hip, knee and chest checks catch scenery above the baked walkable floor.
            foreach (float dy in LocalStepHeights)
                if (!GeometryClear(from + new Vector3(0f, dy, 0f), dir, length + 0.15f)) return false;
            return true;
        }

        // 0 = still stepping, +1 = reached, -1 = stopped. Its progress clock is independent
        // of navmesh rebuilds and path replans, so contact cannot renew the maneuver forever.
        private static int TickLocalStep(Order o, bool upright, float now)
        {
            Vector3 me = LiveCentre(o.Body), delta = o.LocalGoal - me;
            float left = FruitNav.Flat(delta);
            if (!upright || now >= o.LocalDeadline) return EndLocalStep(o, now, -1);
            if (left <= 0.18f && ArrivalClear(me, o.LocalGoal))
            {
                // Let the native pose and physical body settle together before restoring
                // idle footing/support. A single frame inside the radius is not a stop.
                if (o.LocalSettleAt == 0f) o.LocalSettleAt = now;
                o.Runtime.UpdateTargetYRot(o.LocalYaw);
                o.Runtime.SetTargetMoveDirection(Vector3.zero);
                if (now - o.LocalSettleAt >= 0.4f) return EndLocalStep(o, now, 1);
                return 0;
            }
            o.LocalSettleAt = 0f;
            if (Math.Abs(Mathf.DeltaAngle(o.Runtime.CreatureYRotation, o.LocalYaw)) > 35f)
                return EndLocalStep(o, now, -1);
            if (now >= o.LocalCheckAt)
            {
                if (!LocalLegClear(o.Body, me, o.LocalGoal)) return EndLocalStep(o, now, -1);
                o.LocalCheckAt = now + 0.12f;
            }
            if (now >= o.LocalProgressAt)
            {
                if (o.LocalLeft - left < 0.08f) return EndLocalStep(o, now, -1);
                o.LocalLeft = left; o.LocalProgressAt = now + 2f;
            }
            o.Dir = FlatDirection(delta);
            // Preserve separating motion even when already touching another body; halt when
            // a new person crosses the intended step. Never skip the person being approached.
            if (!TrafficClear(o.Body, null, me, o.Dir, Math.Max(0.9f, FlatSpeed(o))))
            { Release(o); return 0; }
            if (!o.Requested)
            {
                if (!o.Walk.HoldsWalkRequest(Holder)) o.Walk.AddWalkRequest(Holder);
                o.Requested = true; o.StartAt = now; ApplyFooting(o);
            }
            o.PaceWanted = 0.8f; SetPace(o, o.PaceWanted);
            o.Runtime.UpdateTargetYRot(o.LocalYaw);
            Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, o.Runtime.CreatureYRotation, 0f)) * o.Dir;
            local.y = 0f;
            float approach = Mathf.Clamp(left / 0.65f, 0.25f, 1f);
            o.Runtime.SetTargetMoveDirection(local.normalized * (RampMagnitude(o, now) * approach));
            return 0;
        }

        private static int EndLocalStep(Order o, float now, int result)
        {
            Release(o); o.LocalStep = false; o.PathAt = -100f; o.Pivot = false;
            o.ProgressAt = now + ProgressWindow; o.ProgressLeft = float.MaxValue;
            if (result < 0) FruitLog.Info($"[FruitBodyControl] #{o.Body.Id} local step stopped with {FruitNav.Flat(o.LocalGoal - LiveCentre(o.Body)):0.00} m left.");
            return result;
        }
    }
}
