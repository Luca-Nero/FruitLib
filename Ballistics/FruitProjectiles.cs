using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// Rounds in flight. The integrator and surface rules are GunsGunsGuns' AkProjectiles;
    /// what is new is that nothing about a round's damage is a free parameter any more:
    ///
    /// - It slows under real quadratic drag (a = -k|v|v, k = ½ρ·Cd·A/m) as well as gravity.
    /// - Its wound power is muzzle power x (v/v0)², i.e. it tracks kinetic energy, so drag,
    ///   ricochets and bodies already passed through all cost what they physically should.
    /// - Bodies are walked by <see cref="FruitWounds"/>, the game's own wound model, and the
    ///   round leaves at the speed the power it has left corresponds to.
    ///
    /// A limb, once entered, is ignored by that round from then on: the voxel walk has already
    /// found the exit, and the limb's collider is usually bigger than its voxels.
    /// </summary>
    internal static class FruitProjectiles
    {
        private const int MaxRounds = 512;

        /// <summary>Ejecta chunks sit on Ignore Raycast; rounds should not hit them.</summary>
        private const int HitMask = ~(1 << 2);

        private static readonly List<Projectile> _rounds = new List<Projectile>();
        private static int _nextId;

        internal static IReadOnlyList<Projectile> Live => _rounds;

        internal static Projectile Spawn(ProjectileSpec spec, BallisticsCommand cmd, bool cosmetic)
        {
            if (_rounds.Count >= MaxRounds) End(0);

            var p = new Projectile
            {
                Id          = ++_nextId,
                Spec        = spec,
                Position    = cmd.Origin,
                Velocity    = cmd.Direction.normalized * Mathf.Max(1f, spec.MuzzleVelocity),
                Alive       = true,
                Cosmetic    = cosmetic,
                MuzzlePower = spec.MuzzlePower,
                Rng         = new System.Random(cmd.Seed),
            };
            _rounds.Add(p);
            FruitBallistics.RaiseSpawned(p);
            return p;
        }

        internal static void Clear()
        {
            for (int i = _rounds.Count - 1; i >= 0; i--) End(i);
        }

        internal static void Tick()
        {
            if (_rounds.Count == 0) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            for (int i = _rounds.Count - 1; i >= 0; i--)
            {
                var r = _rounds[i];
                try
                {
                    if (!Step(r, dt)) End(i);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[FruitBallistics] round {r.Spec?.Id} failed and was removed: {e.Message}");
                    End(i);
                }
            }
        }

        /// <summary>Advances one round a frame. False when it is finished.</summary>
        private static bool Step(Projectile r, float dt)
        {
            var s = r.Spec;
            r.Age += dt;
            if (r.Age > s.Lifetime) return false;

            // Integrate. Drag is applied implicitly (v / (1 + k|v|dt)) so a light, fast
            // fragment in a long frame slows down rather than reversing.
            Vector3 v = r.Velocity;
            v += Physics.gravity * (s.GravityScale * dt);
            if (s.ExternalForces && FruitForces.Any) v += FruitForces.SampleAt(r.Position) * dt;
            v /= 1f + s.DragK * v.magnitude * dt;
            r.Velocity = v;

            if (r.PowerRatio < s.KillPowerRatio) return false;

            // A round can cross several things in one frame: a limb, out the far side, into
            // the next. Keep going until this frame's distance is used up.
            float remaining = v.magnitude * dt;
            for (int guard = 0; guard < 8 && remaining > 0.0001f; guard++)
            {
                Vector3 dir = r.Velocity.normalized;
                if (!Cast(r, dir, remaining, out RaycastHit hit))
                {
                    r.Position += dir * remaining;
                    return true;
                }

                remaining -= hit.distance;
                var limb = FruitWounds.LimbOf(hit.collider);
                bool alive = limb != null ? HitLimb(r, hit, limb, dir) : HitSurface(r, hit, dir);
                if (!alive) return false;

                remaining = Mathf.Max(0f, remaining - 0.02f);
            }
            return true;
        }

        private static bool Cast(Projectile r, Vector3 dir, float dist, out RaycastHit best)
        {
            // The allocating overload, on purpose. The NonAlloc physics calls silently report
            // nothing through this game's IL2CPP bindings (FruitLab found the same with
            // OverlapSphereNonAlloc) - the first version of this used RaycastNonAlloc, and
            // every round flew straight through everything with no error at all.
            best = default;
            var hits = Physics.RaycastAll(r.Position, dir, dist, HitMask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.distance >= bestDist) continue;
                var body = FruitWounds.BodyOf(h.collider);
                if (body != null && r.IgnoredBodies.Contains(body.Pointer)) continue;
                best = h; bestDist = h.distance; found = true;
            }
            return found;
        }

        private static bool HitLimb(Projectile r, RaycastHit hit, Il2CppEffectors.LimbEffectorReceiver limb, Vector3 dir)
        {
            var s = r.Spec;
            var body = FruitWounds.BodyOf(hit.collider);
            if (body != null) r.IgnoredBodies.Add(body.Pointer);

            int powerIn = r.Power;
            var res = FruitWounds.Channel(limb, body, hit.point, hit.normal, dir, powerIn, r.MuzzlePower,
                                          s.Wound, FruitWounds.RoundHit(r.Id), r.Rng,
                                          firstBody: !r.WalkedBody, cosmetic: r.Cosmetic);

            if (!res.Touched)
            {
                // Nothing there - through an existing hole. Carry on unchanged.
                r.Position = hit.point + dir * 0.01f;
                return true;
            }

            r.WalkedBody = true;

            float ratio = res.PowerOut / (float)Mathf.Max(1, r.MuzzlePower);
            bool flies = res.Exited && ratio >= s.KillPowerRatio;
            if (flies)
            {
                Vector3 outDir = Deflect(r, dir, s.PenetrationDeflect);
                // Power tracks energy, so speed goes with its square root.
                r.Velocity = outDir * (s.MuzzleVelocity * Mathf.Sqrt(ratio));
                r.Position = res.Exit + outDir * 0.03f;
            }

            // After the exit is applied, so a listener reading the round sees it leaving.
            FruitBallistics.RaiseWounded(new WoundInfo
            {
                Projectile = r, Limb = limb.gameObject, Entry = hit.point, Exit = res.Exit,
                Direction = dir, PowerIn = powerIn, PowerOut = res.PowerOut,
                Exited = res.Exited, Steps = res.Steps, Cosmetic = r.Cosmetic,
            });
            return flies;
        }

        private static bool HitSurface(Projectile r, RaycastHit hit, Vector3 dir)
        {
            var s = r.Spec;
            float ratio = r.PowerRatio;

            if (!r.Cosmetic && hit.collider.attachedRigidbody != null && s.WorldImpulse > 0f)
            {
                try { hit.collider.attachedRigidbody.AddForceAtPosition(dir * (s.WorldImpulse * ratio), hit.point, ForceMode.Impulse); }
                catch { }
            }

            float incidence = Vector3.Angle(-dir, hit.normal);
            bool ricochet = incidence >= s.RicochetAngle && r.Bounces < s.MaxBounces;

            FruitBallistics.RaiseSurfaceHit(new SurfaceHitInfo
            {
                Projectile = r, Collider = hit.collider, Point = hit.point, Normal = hit.normal,
                Direction = dir, Incidence = incidence, Ricocheted = ricochet, PowerRatio = ratio,
            });

            if (!ricochet) return false;

            r.Bounces++;
            Vector3 outDir = Deflect(r, Vector3.Reflect(dir, hit.normal), s.RicochetScatter);
            // Energy loss, so speed keeps sqrt of what is left.
            float speed = r.Velocity.magnitude * Mathf.Sqrt(Mathf.Clamp01(1f - s.RicochetEnergyLoss));
            r.Velocity = outDir * speed;
            r.Position = hit.point + hit.normal * 0.02f;
            return true;
        }

        /// <summary>A random yaw of up to <paramref name="degrees"/> about the travel direction itself -
        /// not about world axes, which made GunsGunsGuns' scatter lopsided depending on aim.</summary>
        private static Vector3 Deflect(Projectile r, Vector3 dir, float degrees)
        {
            if (degrees <= 0f) return dir;
            Vector3 side = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
            float spin  = (float)r.Rng.NextDouble() * 360f;
            float angle = (float)r.Rng.NextDouble() * degrees;
            Vector3 axis = Quaternion.AngleAxis(spin, dir) * side;
            return (Quaternion.AngleAxis(angle, axis) * dir).normalized;
        }

        private static void End(int index)
        {
            var r = _rounds[index];
            _rounds.RemoveAt(index);
            r.Alive = false;
            FruitBallistics.RaiseEnded(r);
        }
    }
}
