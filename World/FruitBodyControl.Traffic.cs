using System;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        // Predict contact before limbs interlock. A heading nudge alone still has full forward
        // drive; when every available heading is occupied, release the walk instead.
        private static Vector3 Traffic(Order o, FruitBody target, Vector3 me, Vector3 direction, float now)
        {
            float speed = Math.Max(o.Run ? 2.3f : 1.5f, FlatSpeed(o));
            bool clear = TrafficClear(o.Body, target, me, direction, speed);
            if (!clear && !o.Tight && FruitNav.Ready)
            {
                Vector3 right = new Vector3(direction.z, 0f, -direction.x);
                // Both approaching walkers prefer their own right. Try the other side when
                // furniture or people occupy it, always checking the whole navmesh chord.
                for (int angle = 35; angle <= 85 && !clear; angle += 25)
                {
                    float radians = angle * Mathf.Deg2Rad;
                    for (int side = 1; side >= -1; side -= 2)
                    {
                        Vector3 candidate = direction * MathF.Cos(radians) + right * (side * MathF.Sin(radians));
                        if (!TrafficClear(o.Body, target, me, candidate, speed)) continue;
                        Vector3 end = me + candidate * (speed * 1.2f);
                        if (!FruitNav.Under(end, 0.5f, out Vector3 floor) ||
                            FruitNav.Flat(floor - end) > 0.15f || Math.Abs(floor.y - me.y) > 1.6f ||
                            !FruitNav.StraightClear(me, floor)) continue;
                        direction = candidate;
                        clear = true;
                        break;
                    }
                }
            }
            if (!clear && !o.TrafficBlocked) o.TrafficSince = now;
            o.TrafficBlocked = !clear;
            return direction;
        }

        private static bool TrafficClear(FruitBody self, FruitBody target, Vector3 me, Vector3 direction, float speed)
        {
            foreach (var b in FruitBodies.All)
            {
                if (b == self || b == target || !b.HasPosition || Math.Abs(b.Position.y - me.y) > 1.6f) continue;
                Vector3 rel = b.Position - me; rel.y = 0f;
                if (rel.sqrMagnitude < 0.0001f) rel = new Vector3(b.Id > self.Id ? 0.01f : -0.01f, 0f, 0f);
                Vector3 velocity = Vector3.zero;
                if (_orders.TryGetValue(b.Id, out var other) && other.ComInit) velocity = other.ComVel;
                velocity -= direction * speed; velocity.y = 0f;
                float closing = rel.x * velocity.x + rel.z * velocity.z;
                // Existing contact must be allowed to separate. Bodies behind moving away
                // must not stop a walker, including when their centres nearly coincide.
                if (closing >= 0f) continue;
                float t = velocity.sqrMagnitude > 0.0001f ? Math.Clamp(-closing / velocity.sqrMagnitude, 0f, 1.2f) : 0f;
                float space = Upright(b) ? Math.Max(1f, PersonalSpace) : LyingSpace;
                if ((rel + velocity * t).sqrMagnitude < space * space) return false;
            }
            return true;
        }
    }
}
