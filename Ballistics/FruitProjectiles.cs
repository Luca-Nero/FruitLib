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
    /// - It slows under real quadratic drag (a = -k|v|v, k = ½ρ·Cd·A/m, or the G7 curve) against
    ///   the air around it - so wind drifts it - as well as gravity and any FruitForces field.
    /// - Its wound power is muzzle power x (v/v0)², i.e. it tracks kinetic energy, so drag,
    ///   ricochets, walls and bodies already passed through all cost what they physically should.
    /// - Bodies are walked by <see cref="FruitWounds"/>, the game's own wound model, and the
    ///   round leaves at the speed the power it has left corresponds to.
    /// - Surfaces are a <see cref="SurfaceMaterial"/>: it ricochets, goes through (Poncelet) or
    ///   stops, and a round that went through something tumbles from then on.
    ///
    /// A limb, once entered, is ignored by that round from then on: the voxel walk has already
    /// found the exit, and the limb's collider is usually bigger than its voxels.
    /// </summary>
    internal static class FruitProjectiles
    {
        private const int MaxRounds = 512;

        /// <summary>Ejecta chunks sit on Ignore Raycast; rounds should not hit them.</summary>
        private const int HitMask = ~(1 << 2);

        /// <summary>A tumbling round flies side-on: roughly three times the drag.</summary>
        private const float TumbleDrag = 3f;
        /// <summary>Losing more than this fraction of its speed in a surface knocks a round off its axis.</summary>
        private const float TumbleAfterLoss = 0.15f;
        /// <summary>Degrees either side of the ricochet angle over which a ricochet goes from never to always.</summary>
        private const float RicochetBand = 4f;

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
                Owner       = cmd.Owner,
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
                // A listener can end a round, or fire new ones, from inside this loop.
                if (i >= _rounds.Count) continue;
                var r = _rounds[i];
                try
                {
                    if (!Step(r, dt)) End(_rounds.IndexOf(r));
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[FruitBallistics] round {r.Spec?.Id} failed and was removed: {e.Message}");
                    End(_rounds.IndexOf(r));
                }
            }
        }

        /// <summary>Advances one round a frame. False when it is finished.</summary>
        private static bool Step(Projectile r, float dt)
        {
            if (r.Killed) return false;
            var s = r.Spec;
            r.Age += dt;
            if (r.Age > s.Lifetime) return false;

            if (FruitBallistics.WantsStep)
            {
                FruitBallistics.RaiseStep(r, dt);
                if (r.Killed) return false;
            }

            // Integrate. Drag works on the round's speed through the air, so a wind moves it
            // by how much the air pushes on it. It is applied implicitly (v / (1 + k|v|dt)) so
            // a light, fast fragment in a long frame slows down rather than reversing.
            Vector3 v = r.Velocity;
            v += Physics.gravity * (s.GravityScale * dt);
            Vector3 wind = Vector3.zero;
            if (s.ExternalForces)
            {
                if (FruitForces.Any)
                    v += FruitForces.Accelerate(new ForceQuery
                    {
                        Position = r.Position, Velocity = v, MassKg = s.MassKg, Dt = dt, Projectile = r,
                    }) * dt;
                if (FruitForces.AnyWind) wind = FruitForces.WindAt(r.Position);
            }
            Vector3 air   = v - wind;
            float   speed = air.magnitude;
            float   k     = s.DragKAt(speed) * (r.Tumbling ? TumbleDrag : 1f);
            v = air / (1f + k * speed * dt) + wind;
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
                float through = 0f;
                var limb = FruitWounds.LimbOf(hit.collider);
                bool alive = limb != null ? HitLimb(r, hit, limb, dir) : HitSurface(r, hit, dir, out through);
                if (!alive || r.Killed) return false;

                remaining = Mathf.Max(0f, remaining - 0.02f - through);
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
                if (r.IgnoredColliders != null && r.IgnoredColliders.Contains(h.collider.GetInstanceID())) continue;
                var body = FruitWounds.BodyOf(h.collider);
                if (body != null && r.IgnoredBodies.Contains(body.Pointer)) continue;
                best = h; bestDist = h.distance; found = true;
            }
            return found;
        }

        // ── Bodies ───────────────────────────────────────────────────────────────

        private static bool HitLimb(Projectile r, RaycastHit hit, Il2CppEffectors.LimbEffectorReceiver limb, Vector3 dir)
        {
            var s = r.Spec;
            var body = FruitWounds.BodyOf(hit.collider);
            if (body != null) r.IgnoredBodies.Add(body.Pointer);

            int powerIn = r.Power;
            int initial = Mathf.Max(1, Mathf.RoundToInt(r.MuzzlePower * r.PowerScale));
            var res = FruitWounds.Channel(limb, body, hit.point, hit.normal, dir, powerIn, initial,
                                          r.Tumbling ? Tumbled(s.Wound) : s.Wound, FruitWounds.RoundHit(r.Id), r.Rng,
                                          firstBody: !r.WalkedBody, cosmetic: r.Cosmetic);

            if (!res.Touched)
            {
                // Nothing there - through an existing hole. Carry on unchanged.
                r.Position = hit.point + dir * 0.01f;
                return true;
            }

            r.WalkedBody = true;

            float ratio = res.PowerOut / (float)initial;
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

        /// <summary>
        /// A round that arrives side-on has no neck to its wound track and tears a wider
        /// channel. A copy per hit: limb hits are rare, and a mod may change its spec's wound
        /// at any time.
        /// </summary>
        private static WoundProfile Tumbled(WoundProfile w)
        {
            var t = w.Clone();
            t.CleanEntryDepth = 0;
            t.SpreadChance    = Mathf.Min(1f, w.SpreadChance * 1.5f + 0.1f);
            if (w.CavitationPeakRadius > 0) t.CavitationPeakRadius = w.CavitationPeakRadius + 1;
            return t;
        }

        // ── Surfaces ─────────────────────────────────────────────────────────────

        /// <param name="through">Metres of surface crossed, when it went through.</param>
        private static bool HitSurface(Projectile r, RaycastHit hit, Vector3 dir, out float through)
        {
            through = 0f;
            var s = r.Spec;
            float speedIn = r.Velocity.magnitude;
            float incidence = Vector3.Angle(-dir, hit.normal);

            var d = new SurfaceDecision
            {
                Projectile = r, Collider = hit.collider, Point = hit.point, Normal = hit.normal,
                Direction = dir, Incidence = incidence,
                Material = FruitSurfaces.Resolve(hit.collider), Outcome = SurfaceOutcome.Physics,
            };
            Vector3 posBefore = r.Position, velBefore = r.Velocity;
            FruitBallistics.RaiseBeforeSurfaceHit(ref d);

            var info = new SurfaceHitInfo
            {
                Projectile = r, Collider = hit.collider, Point = hit.point, Normal = hit.normal,
                Direction = dir, Incidence = incidence, PowerRatio = r.PowerRatio,
                Material = d.Material ?? FruitSurfaces.Concrete, SpeedIn = speedIn, Outcome = d.Outcome,
            };

            bool alive;
            switch (d.Outcome)
            {
                case SurfaceOutcome.PassThrough:
                    (r.IgnoredColliders ??= new HashSet<int>()).Add(hit.collider.GetInstanceID());
                    r.Position = hit.point;
                    info.SpeedOut = speedIn;
                    alive = true;
                    break;

                case SurfaceOutcome.Handled:
                    if (r.Position == posBefore && r.Velocity == velBefore) r.Kill();
                    info.SpeedOut = r.Killed ? 0f : r.Velocity.magnitude;
                    alive = !r.Killed;
                    break;

                case SurfaceOutcome.Stop:
                    alive = Stop(r, hit, dir, ref info);
                    break;

                case SurfaceOutcome.Ricochet:
                    alive = Ricochet(r, hit, info.Material, ref info);
                    break;

                default:
                    alive = Physical(r, hit, dir, ref info, out through);
                    break;
            }

            FruitBallistics.RaiseSurfaceHit(info);
            return alive && !r.Killed;
        }

        /// <summary>Ricochet if the angle says so, else through if it has the energy, else stop.</summary>
        private static bool Physical(Projectile r, RaycastHit hit, Vector3 dir, ref SurfaceHitInfo info, out float through)
        {
            through = 0f;
            var s = r.Spec;
            var m = info.Material;

            float chance = m.RicochetChance(s.RicochetAngle, info.Incidence, RicochetBand);
            if (r.Bounces < s.MaxBounces && chance > 0f && r.Rng.NextDouble() < chance)
                return Ricochet(r, hit, m, ref info);

            if (Penetrate(r, hit, dir, m, ref info, out through)) return true;
            return Stop(r, hit, dir, ref info);
        }

        private static bool Penetrate(Projectile r, RaycastHit hit, Vector3 dir, SurfaceMaterial m,
                                      ref SurfaceHitInfo info, out float through)
        {
            through = 0f;
            var s = r.Spec;
            if (!FruitLibConfig.WallPenetration || s.PenetrationScale <= 0f || !m.Penetrable) return false;

            // A tumbling round meets the surface side-on: about half the sectional density.
            float sd = s.SectionalDensity * s.PenetrationScale * Mathf.Max(0f, FruitLibConfig.PenetrationScale)
                       * (r.Tumbling ? 0.5f : 1f);
            float speedIn = info.SpeedIn;
            float reach   = Mathf.Min(m.Depth(sd, speedIn), m.MaxThickness);
            if (reach < 0.002f) return false;

            // Nothing on the far side within reach means the surface is thicker than that - or
            // the ray started inside it - and either way the round stays in.
            if (!FruitSurfaces.FarSide(hit.collider, hit.point, dir, reach, out Vector3 exit, out float thickness)) return false;

            float vOut = m.ExitSpeed(sd, speedIn, thickness);
            float v0 = Mathf.Max(1f, s.MuzzleVelocity);
            if (vOut <= 0f || (vOut / v0) * (vOut / v0) < s.KillPowerRatio) return false;

            float lost = 1f - vOut / Mathf.Max(0.01f, speedIn);
            Vector3 outDir = Deflect(r, dir, m.ExitScatter * lost + s.PenetrationDeflect);
            Push(r, hit, dir * (speedIn - vOut));

            r.Velocity = outDir * vOut;
            r.Position = exit + dir * 0.01f;
            r.Penetrations++;
            if (lost > TumbleAfterLoss) r.Tumbling = true;

            info.Penetrated = true;
            info.Exit       = exit;
            info.Thickness  = thickness;
            info.SpeedOut   = vOut;
            through = thickness;
            return true;
        }

        /// <summary>
        /// Off the surface: the part of the velocity along it mostly survives (Grip), the part
        /// into it mostly does not (Restitution), so a round leaves flatter than it came in.
        /// Speed then follows the energy the round and surface lose between them.
        /// </summary>
        private static bool Ricochet(Projectile r, RaycastHit hit, SurfaceMaterial m, ref SurfaceHitInfo info)
        {
            var s = r.Spec;
            r.Bounces++;

            Vector3 v = r.Velocity;
            Vector3 outDir = KeepOff(Deflect(r, m.Bounce(v, hit.normal).normalized, s.RicochetScatter), hit.normal);

            float keep  = Mathf.Sqrt(Mathf.Clamp01(1f - s.RicochetEnergyLoss * m.RicochetLossScale));
            float speed = v.magnitude * keep;
            r.Velocity = outDir * speed;
            r.Position = hit.point + hit.normal * 0.02f;
            Push(r, hit, v - r.Velocity);

            info.Ricocheted = true;
            info.SpeedOut   = speed;
            return true;
        }

        private static bool Stop(Projectile r, RaycastHit hit, Vector3 dir, ref SurfaceHitInfo info)
        {
            Push(r, hit, dir * info.SpeedIn);
            info.SpeedOut = 0f;
            return false;
        }

        /// <summary>
        /// The push the round gives what it hit: <see cref="ProjectileSpec.WorldImpulse"/> for a
        /// full-speed round stopping dead, scaled by the velocity it actually lost there.
        /// </summary>
        private static void Push(Projectile r, RaycastHit hit, Vector3 lostVelocity)
        {
            var s = r.Spec;
            if (r.Cosmetic || s.WorldImpulse <= 0f) return;
            var rb = hit.collider.attachedRigidbody;
            if (rb == null) return;
            try { rb.AddForceAtPosition(lostVelocity * (s.WorldImpulse / Mathf.Max(1f, s.MuzzleVelocity)), hit.point, ForceMode.Impulse); }
            catch { }
        }

        private static Vector3 Deflect(Projectile r, Vector3 dir, float degrees) => Deflect(r.Rng, dir, degrees);

        /// <summary>A random yaw of up to <paramref name="degrees"/> about the travel direction itself -
        /// not about world axes, which made GunsGunsGuns' scatter lopsided depending on aim.</summary>
        internal static Vector3 Deflect(System.Random rng, Vector3 dir, float degrees)
        {
            if (degrees <= 0f) return dir;
            Vector3 side = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
            float spin  = (float)rng.NextDouble() * 360f;
            float angle = (float)rng.NextDouble() * degrees;
            Vector3 axis = Quaternion.AngleAxis(spin, dir) * side;
            return (Quaternion.AngleAxis(angle, axis) * dir).normalized;
        }

        /// <summary>Scatter must not send a ricochet back into the surface it came off.</summary>
        internal static Vector3 KeepOff(Vector3 dir, Vector3 normal)
            => Vector3.Dot(dir, normal) < 0.02f
                ? (Vector3.ProjectOnPlane(dir, normal).normalized + normal * 0.05f).normalized
                : dir;

        private static void End(int index)
        {
            if (index < 0 || index >= _rounds.Count) return;
            var r = _rounds[index];
            _rounds.RemoveAt(index);
            r.Alive = false;
            FruitBallistics.RaiseEnded(r);
        }
    }
}
