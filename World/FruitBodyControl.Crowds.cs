using System;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        // Local passing routes supplement the static navmesh, which does not include people.
        // Two points keep the walker beside the obstacle until it is beyond it. A single sideways
        // nudge is cancelled by path pursuit and can leave a whole queue oscillating in place.
        private static bool PassCrowd(Order o, FruitBody target, Vector3 me, Vector3 forward,
            Vector3 goal, float now, out Vector3 direction)
        {
            direction = forward;
            if (o.PassStage != 0)
            {
                if (now >= o.PassUntil) o.PassStage = 0;
                else
                {
                    if (o.PassStage == 1 && FruitNav.Flat(o.PassBeside - me) < 0.45f) o.PassStage = 2;
                    Vector3 aim = o.PassStage == 1 ? o.PassBeside : o.PassBeyond;
                    if (o.PassStage == 2 && FruitNav.Flat(aim - me) < 0.45f) o.PassStage = 0;
                    else if (PassLegClear(o.Body, me, aim))
                    {
                        direction = aim - me; direction.y = 0f;
                        direction = direction.normalized;
                        return true;
                    }
                    else o.PassStage = 0; // the scene changed or the walker was pushed off course
                }
            }
            if (now < o.PassCheckAt) return false;
            o.PassCheckAt = now + 0.4f;
            float nearest = Math.Min(2.8f, FruitNav.Flat(goal - me));
            FruitBody blocker = null;
            foreach (var b in FruitBodies.All)
            {
                if (b == o.Body || b == target || !b.HasPosition || Math.Abs(b.Position.y - me.y) > 1.6f) continue;
                // Pass stationary people and stalled walkers. Keep ordinary moving traffic on
                // the existing avoidance/yield rules instead of continually overtaking it.
                if (OnItsWay(b) && (!_orders.TryGetValue(b.Id, out var other) || !other.ComInit || FlatSpeed(other) > 0.2f)) continue;
                Vector3 rel = b.Position - me;
                float ahead = rel.x * forward.x + rel.z * forward.z;
                float across = Math.Abs(rel.x * forward.z - rel.z * forward.x);
                if (ahead < 0.2f || ahead >= nearest || across > (Upright(b) ? 0.9f : 1.2f)) continue;
                nearest = ahead;
                blocker = b;
            }
            if (blocker == null) return false;

            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            Vector3 relative = blocker.Position - me;
            float side = relative.x * right.x + relative.z * right.z > 0.05f ? -1f : 1f;
            float clearance = Math.Max(Upright(blocker) ? PersonalSpace : LyingSpace, 0.9f) + 0.4f;
            for (int attempt = 0; attempt < 2; attempt++, side = -side)
            {
                Vector3 centre = blocker.Position; centre.y = me.y;
                Vector3 beside = centre - forward * 0.55f + right * (side * clearance);
                Vector3 beyond = centre + forward * 1.0f + right * (side * clearance);
                if (!PassPoint(beside, o.Body, out beside) || !PassPoint(beyond, o.Body, out beyond)) continue;
                if (!PassLegClear(o.Body, me, beside) || !PassLegClear(o.Body, beside, beyond)) continue;
                o.PassBeside = beside;
                o.PassBeyond = beyond;
                o.PassStage = 1;
                o.PassUntil = now + 6f;
                // Sideways progress is real progress even though it does not shorten the path.
                o.ProgressLeft = float.MaxValue;
                o.ProgressAt = now + ProgressWindow;
                direction = beside - me; direction.y = 0f;
                direction = direction.normalized;
                return true;
            }
            return false;
        }

        private static bool PassPoint(Vector3 point, FruitBody body, out Vector3 floor)
        {
            // Inputs are at pelvis height; Under prefers the floor beneath, not a nearby upper slab.
            if (!FruitNav.Under(point, 0.6f, out floor)) return false;
            return FruitNav.Flat(floor - point) < 0.25f && Math.Abs(floor.y - point.y) < 1.6f &&
                FruitNav.Room(floor) >= TightRoom && !NearDoor(floor) && !TakenByStander(floor, body);
        }

        private static bool PassLegClear(FruitBody self, Vector3 from, Vector3 to)
        {
            if (!FruitNav.StraightClear(from, to)) return false;
            Vector3 way = to - from; way.y = 0f;
            float length2 = way.sqrMagnitude;
            if (length2 < 0.001f) return true;
            foreach (var b in FruitBodies.All)
            {
                if (b == self || !b.HasPosition || Math.Abs(b.Position.y - from.y) > 1.6f) continue;
                if (OnItsWay(b) && (!_orders.TryGetValue(b.Id, out var other) || !other.ComInit || FlatSpeed(other) > 0.2f)) continue;
                Vector3 rel = b.Position - from; rel.y = 0f;
                float t = Math.Clamp((rel.x * way.x + rel.z * way.z) / length2, 0f, 1f);
                // If already shoulder to shoulder, allow a route that moves out of contact.
                float clearance = Math.Min(Upright(b) ? 0.8f : LyingSpace, rel.magnitude);
                if ((rel - way * t).sqrMagnitude < clearance * clearance - 0.001f) return false;
            }
            return true;
        }

        /// <summary>Prefer a free arrival spot with an open final approach over one behind the
        /// person who got there first. Distance alone puts successive arrivals on the same line.</summary>
        private static float ArrivalObstruction(FruitBody self, Vector3 from, Vector3 spot)
        {
            Vector3 way = spot - from; way.y = 0f;
            float length = way.magnitude;
            if (length < 0.01f) return 0f;
            way /= length;
            float penalty = 0f;
            foreach (var b in FruitBodies.All)
            {
                if (b == self || !b.HasPosition || Math.Abs(b.Position.y - spot.y) > 1.6f) continue;
                Vector3 at = b.Position;
                if (OnItsWay(b))
                {
                    if (!_orders.TryGetValue(b.Id, out var other) || !other.HasDest) continue;
                    at = other.Stand;
                }
                Vector3 rel = at - spot;
                float along = -(rel.x * way.x + rel.z * way.z);
                float across = Math.Abs(rel.x * way.z - rel.z * way.x);
                float space = Upright(b) ? PersonalSpace : LyingSpace;
                if (along > 0f && along < Math.Min(3f, length) && across < space)
                    penalty += 4f * (1f - across / space);
            }
            return penalty;
        }
    }
}
