using System;
using System.Collections.Generic;
using Il2CppEffectors;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// BombsAway's detonation, minus its visuals: shockwave push, overpressure damage on nearby
    /// limbs, and a sampled fragment field. The structure is unchanged - golden-angle fragment
    /// directions, elliptical cones, ballistic arcs swept with sphere casts, impulses applied
    /// only after every wound has landed - and the difference is how fragments wound: each one
    /// that reaches a limb goes through <see cref="FruitWounds.Channel"/> with a power that
    /// falls off with distance, instead of the old fixed three-step cone.
    ///
    /// All randomness comes from the command's seed.
    /// </summary>
    internal static class FruitExplosions
    {
        private const string Tag = "[FruitBallistics]";

        private static readonly Dictionary<IntPtr, (Rigidbody rb, Vector3 dv)> _impulses = new Dictionary<IntPtr, (Rigidbody, Vector3)>();
        private static readonly HashSet<IntPtr> _limbsSeen = new HashSet<IntPtr>();
        private static readonly List<(LimbEffectorReceiver limb, Vector3 centre)> _limbs = new List<(LimbEffectorReceiver, Vector3)>();

        internal static void Detonate(ExplosionSpec s, BallisticsCommand cmd, bool cosmetic)
        {
            FruitPerfMon.Begin("FruitExplosions");
            try { DetonateInternal(s, cmd, cosmetic); }
            finally { FruitPerfMon.End("FruitExplosions"); }
        }

        private static void DetonateInternal(ExplosionSpec s, BallisticsCommand cmd, bool cosmetic)
        {
            Vector3 origin  = cmd.Origin;
            Vector3 forward = cmd.Direction.sqrMagnitude > 0f ? cmd.Direction.normalized : Vector3.up;
            var rng = new System.Random(cmd.Seed);

            float quality = s.AdaptiveQuality
                ? Mathf.Lerp(1f, Mathf.Clamp01(s.MinQuality), FruitPerfMon.PressureLevel)
                : 1f;

            _impulses.Clear();

            if (!cosmetic) Shockwave(s, origin, forward);

            int budget = Mathf.Max(1, Mathf.RoundToInt(s.MaxWounds * quality));
            if (!cosmetic) budget = Overpressure(s, origin, forward, quality, budget, rng);

            bool hasGround = Physics.Raycast(origin, Vector3.down, out RaycastHit ground, 200f, s.LayerMask, QueryTriggerInteraction.Ignore);
            FruitBallistics.RaiseExploded(new ExplosionInfo
            {
                Spec = s, Origin = origin, Forward = forward,
                HasGround = hasGround, Ground = ground, Cosmetic = cosmetic,
            });

            Fragments(s, origin, forward, hasGround ? ground.point.y : origin.y - 50f, quality, budget, rng, cosmetic);

            // Last, so no wound is measured against a body that has already been thrown.
            if (!cosmetic)
                foreach (var kv in _impulses.Values)
                    if (kv.rb != null) kv.rb.linearVelocity += kv.dv;
        }

        // ── Shockwave ────────────────────────────────────────────────────────────

        private static void Shockwave(ExplosionSpec s, Vector3 origin, Vector3 forward)
        {
            foreach (var col in Physics.OverlapSphere(origin, s.BlastRadius, s.LayerMask, QueryTriggerInteraction.Ignore))
            {
                var rb = FruitWounds.BodyOf(col);
                if (rb == null || _impulses.ContainsKey(rb.Pointer)) continue;

                Vector3 to   = rb.transform.position - origin;
                float   dist = to.magnitude;
                Vector3 dir  = dist > 0.0001f ? to / dist : Vector3.up;
                float   cone = ConeAttenuation(s, forward, dir);
                if (cone < 0.01f) continue;

                float falloff = (1f - Mathf.Clamp01(dist / s.BlastRadius)) * cone;
                AddImpulse(rb, dir * (s.BlastForce * falloff) + Vector3.up * (s.BlastUpward * falloff));
            }
        }

        // ── Overpressure ─────────────────────────────────────────────────────────

        private static int Overpressure(ExplosionSpec s, Vector3 origin, Vector3 forward, float quality, int budget, System.Random rng)
        {
            if (s.OverpressureRadius <= 0f || s.OverpressurePoints <= 0) return budget;

            _limbsSeen.Clear();
            _limbs.Clear();
            foreach (var col in Physics.OverlapSphere(origin, s.OverpressureRadius, s.LayerMask, QueryTriggerInteraction.Ignore))
            {
                var limb = FruitWounds.LimbOf(col);
                if (limb == null || !_limbsSeen.Add(limb.Pointer)) continue;
                _limbs.Add((limb, limb.transform.position));
            }

            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            int points = Mathf.Max(1, Mathf.RoundToInt(s.OverpressurePoints * quality));

            foreach (var (limb, centre) in _limbs)
            {
                if (budget <= 0) break;

                float dist = Vector3.Distance(origin, centre);
                float cone = ConeAttenuation(s, forward, (centre - origin).normalized);
                float scale = Mathf.Pow(1f - Mathf.Clamp01(dist / s.OverpressureRadius), s.OverpressureFalloffExp) * cone;
                if (scale < 0.05f) continue;

                // Points spread over the limb's surface, found by casting inward from a
                // Fibonacci sphere around it - so the damage wraps the limb, not just its
                // side facing the charge.
                for (int i = 0; i < points && budget > 0; i++)
                {
                    float t  = points > 1 ? i / (float)(points - 1) : 0.5f;
                    float fy = Mathf.Lerp(-1f, 1f, t);
                    float rx = Mathf.Sqrt(Mathf.Max(0f, 1f - fy * fy));
                    float a  = i * golden;
                    Vector3 around = new Vector3(rx * Mathf.Cos(a), fy, rx * Mathf.Sin(a));

                    Vector3 from = centre + around * (s.OverpressureRadius * 0.6f);
                    Vector3 dir  = (centre - from).normalized;
                    if (!Physics.Raycast(from, dir, out RaycastHit hit, s.OverpressureRadius * 1.2f, s.LayerMask, QueryTriggerInteraction.Ignore)) continue;
                    if (FruitWounds.LimbOf(hit.collider)?.Pointer != limb.Pointer) continue;

                    int radius = Mathf.Max(1, Mathf.RoundToInt(s.OverpressureWoundRadius * Mathf.Lerp(0.5f, 1f, scale)));
                    // Into the limb from where the cast met it - the side facing away from the
                    // charge gets hurt less through scale, not skipped.
                    FruitWounds.Burst(limb, hit.point, dir, radius,
                                      s.OverpressureDamage * scale * s.DamageScale, scale, rng);
                    budget--;
                }
            }
            return budget;
        }

        // ── Fragments ────────────────────────────────────────────────────────────

        private static void Fragments(ExplosionSpec s, Vector3 origin, Vector3 forward, float groundY,
                                      float quality, int budget, System.Random rng, bool cosmetic)
        {
            int rays = Mathf.Max(1, Mathf.RoundToInt(s.FragCount * quality));
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            float gy = Physics.gravity.y;

            Quaternion coneRot = s.IsFullSphere ? Quaternion.identity : Quaternion.LookRotation(forward);
            float tanH = Mathf.Tan(Mathf.Min(s.HSpreadDeg * 0.5f, 89f) * Mathf.Deg2Rad);
            float tanV = Mathf.Tan(Mathf.Min(s.VSpreadDeg * 0.5f, 89f) * Mathf.Deg2Rad);

            int maxDebris = s.DebrisRatio > 0f && FruitBallistics.WantsDebris
                ? Mathf.Min(Mathf.RoundToInt(rays * s.DebrisRatio), Mathf.Max(1, Mathf.RoundToInt(s.MaxDebris * quality)))
                : 0;
            int debrisEvery = maxDebris > 0 ? Mathf.Max(1, rays / maxDebris) : int.MaxValue;
            int debris = 0;

            // A random turn of the whole pattern, so two identical charges do not throw
            // fragments down identical lines.
            Quaternion jitter = Quaternion.AngleAxis((float)rng.NextDouble() * 360f, forward);

            for (int r = 0; r < rays; r++)
            {
                float t = rays > 1 ? r / (float)(rays - 1) : 0.5f;
                Vector3 dir;
                if (s.IsFullSphere)
                {
                    float y  = Mathf.Lerp(-1f, 1f, t);
                    float rx = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                    float a  = r * golden;
                    dir = jitter * new Vector3(rx * Mathf.Cos(a), y, rx * Mathf.Sin(a));
                }
                else
                {
                    float az = r * golden;
                    float half = EllipticalHalfAngle(az, tanH, tanV);
                    float th = Mathf.Sqrt(t) * half;
                    dir = jitter * (coneRot * new Vector3(Mathf.Sin(th) * Mathf.Cos(az), Mathf.Sin(th) * Mathf.Sin(az), Mathf.Cos(th)));
                }
                dir.Normalize();

                Vector3 vel = dir * s.FragSpeed;
                Vector3 p0  = origin + dir * 0.04f;
                float flight = BallisticGroundTime(gy, vel.y, p0.y - groundY, s.FragMaxTime);

                bool didHit = SweepArc(s, p0, vel, gy, flight, out RaycastHit hit);

                if (debris < maxDebris && r % debrisEvery == 0)
                {
                    FruitBallistics.RaiseDebris(s, p0, vel,didHit ? hit.distance / Mathf.Max(0.01f, s.FragSpeed) : flight);
                    debris++;
                }

                if (!didHit || cosmetic) continue;
                float dist = Vector3.Distance(origin, hit.point);
                if (dist < 0.02f) continue;

                var rb = FruitWounds.BodyOf(hit.collider);
                if (rb != null && s.FragImpulse > 0f) AddImpulse(rb, dir * s.FragImpulse);

                var limb = FruitWounds.LimbOf(hit.collider);
                if (limb == null || budget <= 0) continue;

                int power = Mathf.RoundToInt(s.FragPower * s.DamageScale * Mathf.Exp(-s.FragPowerFalloff * dist));
                if (power < 1) continue;

                var res = FruitWounds.Channel(limb, rb, hit.point, hit.normal, dir, power, s.FragPower,
                                              s.FragWound, rng, firstBody: true, cosmetic: false);
                budget--;

                if (res.Touched)
                    FruitBallistics.RaiseWounded(new WoundInfo
                    {
                        Limb = limb.gameObject, Entry = hit.point, Exit = res.Exit, Direction = dir,
                        PowerIn = power, PowerOut = res.PowerOut, Exited = res.Exited, Steps = res.Steps,
                    });
            }
        }

        private static bool SweepArc(ExplosionSpec s, Vector3 p0, Vector3 vel, float gy, float flight, out RaycastHit hit)
        {
            hit = default;
            Vector3 landing = p0 + vel * flight + new Vector3(0f, 0.5f * gy * flight * flight, 0f);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(p0, landing) / 0.75f), 2, Mathf.Max(2, s.ArcSteps));

            Vector3 prev = p0;
            for (int i = 1; i <= steps; i++)
            {
                float ft = flight * (i / (float)steps);
                Vector3 pt = p0 + vel * ft + new Vector3(0f, 0.5f * gy * ft * ft, 0f);
                Vector3 seg = pt - prev;
                if (seg.sqrMagnitude > 0f &&
                    Physics.SphereCast(prev, 0.04f, seg.normalized, out hit, seg.magnitude, s.LayerMask, QueryTriggerInteraction.Ignore))
                    return true;
                prev = pt;
            }
            return false;
        }

        // ── Helpers (BombsAway) ─────────────────────────────────────────────────

        private static void AddImpulse(Rigidbody rb, Vector3 dv)
        {
            if (_impulses.TryGetValue(rb.Pointer, out var e)) _impulses[rb.Pointer] = (rb, e.dv + dv);
            else _impulses[rb.Pointer] = (rb, dv);
        }

        internal static float BallisticGroundTime(float gy, float vy, float dy, float maxTime)
        {
            float a = 0.5f * gy, b = vy, c = dy;
            float disc = b * b - 4f * a * c;
            if (disc < 0f || Mathf.Abs(a) < 1e-6f) return maxTime;
            float sq = Mathf.Sqrt(disc);
            float t1 = (-b + sq) / (2f * a), t2 = (-b - sq) / (2f * a);
            float tHit = -1f;
            if (t1 > 0.001f && t2 > 0.001f) tHit = Mathf.Min(t1, t2);
            else if (t1 > 0.001f) tHit = t1;
            else if (t2 > 0.001f) tHit = t2;
            return tHit > 0f ? Mathf.Min(tHit, maxTime) : maxTime;
        }

        private static float EllipticalHalfAngle(float azimuth, float tanH, float tanV)
        {
            float c = Mathf.Cos(azimuth), s = Mathf.Sin(azimuth);
            return Mathf.Atan2(1f, Mathf.Sqrt(c * c / (tanH * tanH) + s * s / (tanV * tanV)));
        }

        private static float ConeAttenuation(ExplosionSpec s, Vector3 forward, Vector3 dir)
        {
            if (s.IsFullSphere) return 1f;

            float cosAngle = Vector3.Dot(dir, forward);
            Vector3 local = Quaternion.Inverse(Quaternion.LookRotation(forward)) * dir;
            float tanH = Mathf.Tan(Mathf.Min(s.HSpreadDeg * 0.5f * Mathf.Deg2Rad, 1.5f));
            float tanV = Mathf.Tan(Mathf.Min(s.VSpreadDeg * 0.5f * Mathf.Deg2Rad, 1.5f));
            float half = EllipticalHalfAngle(Mathf.Atan2(local.y, local.x), tanH, tanV);

            float inner = Mathf.Cos(half);
            float outer = Mathf.Cos(Mathf.Min(half * 1.15f, Mathf.PI));
            if (cosAngle >= inner) return 1f;
            if (cosAngle <= outer) return 0f;
            return Mathf.InverseLerp(outer, inner, cosAngle);
        }
    }
}
