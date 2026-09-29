using System;
using System.Collections.Generic;
using Il2CppEffectors;
using Il2CppEffectors.ReceiveMethods.Index;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// BombsAway's detonation, minus its visuals: shockwave push, overpressure damage on nearby
    /// limbs, and a sampled fragment field. The structure is unchanged - golden-angle fragment
    /// directions, elliptical cones, ballistic arcs swept with sphere casts, impulses applied
    /// only after every wound has landed.
    ///
    /// What changed is that fragments and blast now meet the world the way rounds do:
    /// - A fragment is a steel chunk with a real speed (from its power and mass) and sectional
    ///   density. At a surface it ricochets, goes through (Poncelet, <see cref="SurfaceMaterial"/>)
    ///   or stops, with the same rules as <see cref="FruitProjectiles"/>; out of a limb it carries
    ///   on with what the wound walk left it. Each of those starts a new leg of its arc, up to
    ///   <see cref="MaxLegs"/>, and its wound power follows its speed squared throughout.
    /// - The shockwave and overpressure are cut by cover between the charge and each target.
    ///
    /// The arc's shape still uses the spec's slow, readable FragSpeed; a leg after a loss of
    /// speed flies a correspondingly shorter arc.
    ///
    /// All randomness comes from the command's seed.
    /// </summary>
    internal static class FruitExplosions
    {
        private const string Tag = "[FruitBallistics]";

        /// <summary>Legs per fragment: the flight from the charge plus ricochets, walls and limbs passed through.</summary>
        private const int MaxLegs = 4;
        /// <summary>Below this fraction of its starting power a fragment is spent.</summary>
        private const float KillPowerRatio = 0.02f;
        /// <summary>Degrees either side of the ricochet angle over which a ricochet goes from never to always.</summary>
        private const float RicochetBand = 5f;
        /// <summary>Random yaw leaving a limb, degrees: fragments tumble.</summary>
        private const float LimbExitDeflect = 4f;
        /// <summary>Radius of the swept fragment, metres.</summary>
        private const float FragRadius = 0.04f;
        /// <summary>Legs for a jet ray, which goes through wall after wall.</summary>
        private const int MaxJetLegs = 8;
        /// <summary>Arc speed of a jet ray at full power, m/s: fast enough that the drawn arc is a straight line.</summary>
        private const float JetArcSpeed = 300f;
        /// <summary>Anything with no dimension over this (metres) is too small to shield from a blast.</summary>
        private const float MinCoverSize = 1f;

        /// <summary>
        /// One detonation's state. Its own object, not statics: a listener may set off another
        /// charge from inside any of the events raised mid-detonation (a chain reaction).
        /// </summary>
        private sealed class Blast
        {
            public readonly Dictionary<IntPtr, (Rigidbody rb, Vector3 dv)> Impulses = new Dictionary<IntPtr, (Rigidbody, Vector3)>();
            public readonly HashSet<IntPtr> LimbsSeen = new HashSet<IntPtr>();
            public readonly List<(LimbEffectorReceiver limb, Rigidbody body, Vector3 centre)> Limbs = new List<(LimbEffectorReceiver, Rigidbody, Vector3)>();
            /// <summary>Bodies the current fragment has already been through.</summary>
            public readonly List<IntPtr> Passed = new List<IntPtr>();
            /// <summary>Walls a jet has blown spall off (collider ids), and the spall still to throw.</summary>
            public readonly HashSet<int> Spalled = new HashSet<int>();
            public readonly List<Scab> Spall = new List<Scab>();
            public float          SpallQuality = 1f;

            public ExplosionSpec  S;
            public BallisticsCommand Cmd;
            public bool           Cosmetic;
            public Vector3        Origin;
            public float          GroundY;
            public EffectorHit    Shot;
            public System.Random  Rng;
            public int            Budget, SecondaryBudget;
            /// <summary>Bone thrown out of exit wounds, still to fly: where, which way, power.</summary>
            public readonly List<(Vector3 at, Vector3 dir, float power)> Bones = new List<(Vector3, Vector3, float)>();
            public float          V0, SD;
            public bool           Trace;
        }

        internal static void Detonate(ExplosionSpec s, BallisticsCommand cmd, bool cosmetic)
        {
            FruitPerfMon.Lib.Begin("Explosions");
            try { DetonateInternal(s, cmd, cosmetic); }
            finally { FruitPerfMon.Lib.End("Explosions"); }
        }

        private static void DetonateInternal(ExplosionSpec s, BallisticsCommand cmd, bool cosmetic)
        {
            Vector3 origin  = cmd.Origin;
            Vector3 forward = cmd.Direction.sqrMagnitude > 0f ? cmd.Direction.normalized : Vector3.up;
            var rng = new System.Random(cmd.Seed);
            // One shot, however many fragments: the game's shotgun pellets share theirs too.
            var shot = FruitWounds.NextBlastHit();

            float quality = s.AdaptiveQuality
                ? Mathf.Lerp(1f, Mathf.Clamp01(s.MinQuality), FruitPerfMon.PressureLevel)
                : 1f;

            var b = new Blast { S = s, Cmd = cmd, Cosmetic = cosmetic, Origin = origin, Shot = shot, Rng = rng };

            if (!cosmetic) Shockwave(b, forward);

            int budget = Mathf.Max(1, Mathf.RoundToInt(s.MaxWounds * quality));
            if (!cosmetic) budget = Overpressure(b, forward, quality, budget);

            bool hasGround = Physics.Raycast(origin, Vector3.down, out RaycastHit ground, 200f, s.LayerMask, QueryTriggerInteraction.Ignore);
            FruitBallistics.RaiseExploded(new ExplosionInfo
            {
                Spec = s, Origin = origin, Forward = forward,
                HasGround = hasGround, Ground = ground, Cosmetic = cosmetic, Owner = cmd.Owner,
            });

            b.GroundY = hasGround ? ground.point.y : origin.y - 50f;
            b.Budget = budget;
            // The jet and its spall come last, so they get a budget of their own rather than
            // whatever the fragment field left over.
            b.SecondaryBudget = s.JetRays > 0 || s.BoneFragments > 0
                ? Mathf.Max(1, Mathf.RoundToInt(s.SecondaryMaxWounds * quality)) : 0;
            b.V0 = s.FragVelocity;
            b.SD = s.FragSectionalDensity * Mathf.Max(0f, s.FragPenetrationScale) * Mathf.Max(0f, FruitLibConfig.PenetrationScale);
            b.Trace = FruitBallistics.WantsFragments;

            Fragments(b, forward, quality);

            // Last, so no wound is measured against a body that has already been thrown.
            if (!cosmetic)
                foreach (var kv in b.Impulses.Values)
                    if (kv.rb != null) kv.rb.linearVelocity += kv.dv;
        }

        // ── Shockwave ────────────────────────────────────────────────────────────

        private static void Shockwave(Blast b, Vector3 forward)
        {
            var s = b.S;
            Vector3 origin = b.Origin;
            foreach (var col in Physics.OverlapSphere(origin, s.BlastRadius, s.LayerMask, QueryTriggerInteraction.Ignore))
            {
                var rb = FruitWounds.BodyOf(col);
                if (rb == null || b.Impulses.ContainsKey(rb.Pointer)) continue;

                Vector3 pos  = rb.transform.position;
                Vector3 to   = pos - origin;
                float   dist = to.magnitude;
                Vector3 dir  = dist > 0.0001f ? to / dist : Vector3.up;
                float   cone = ConeAttenuation(s, forward, dir);
                if (cone < 0.01f) continue;

                float cover = Cover(b, pos, rb, overpressure: false);
                float falloff = (1f - Mathf.Clamp01(dist / s.BlastRadius)) * cone * cover;
                AddImpulse(b, rb, dir * (s.BlastForce * falloff) + Vector3.up * (s.BlastUpward * falloff));
            }
        }

        // ── Overpressure ─────────────────────────────────────────────────────────

        private static int Overpressure(Blast b, Vector3 forward, float quality, int budget)
        {
            var s = b.S;
            Vector3 origin = b.Origin;
            if (s.ChargeKgTNT > 0f) return BlastWave(b, forward, quality, budget);
            if (s.OverpressureRadius <= 0f || s.OverpressurePoints <= 0) return budget;

            foreach (var col in Physics.OverlapSphere(origin, s.OverpressureRadius, s.LayerMask, QueryTriggerInteraction.Ignore))
            {
                var limb = FruitWounds.LimbOf(col);
                if (limb == null || !b.LimbsSeen.Add(limb.Pointer)) continue;
                b.Limbs.Add((limb, FruitWounds.BodyOf(col), limb.transform.position));
            }

            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            int points = Mathf.Max(1, Mathf.RoundToInt(s.OverpressurePoints * quality));

            // Overpressure may take only its share of the budget, or a charge among a crowd
            // spends it all on surface damage and every fragment after it cuts nothing.
            int cap = Mathf.Min(budget, Mathf.Max(1, Mathf.CeilToInt(budget * Mathf.Clamp01(s.OverpressureBudgetShare))));
            int used = 0;

            var targets = new List<(LimbEffectorReceiver limb, Vector3 centre, float scale)>(b.Limbs.Count);
            foreach (var (limb, body, centre) in b.Limbs)
            {
                float dist = Vector3.Distance(origin, centre);
                float cone = ConeAttenuation(s, forward, (centre - origin).normalized);
                float scale = Mathf.Pow(1f - Mathf.Clamp01(dist / s.OverpressureRadius), s.OverpressureFalloffExp) * cone;
                if (scale < 0.05f) continue;

                scale *= Cover(b, centre, body, overpressure: true);
                if (scale >= 0.05f) targets.Add((limb, centre, scale));
            }

            // Point by point across every limb, not limb by limb: when the cap bites, every
            // limb in range still gets some, instead of the first few getting it all.
            // Points spread over each limb's surface, found by casting inward from a Fibonacci
            // sphere around it - so the damage wraps the limb, not just its side facing the charge.
            for (int i = 0; i < points && used < cap; i++)
            {
                float t  = points > 1 ? i / (float)(points - 1) : 0.5f;
                float fy = Mathf.Lerp(-1f, 1f, t);
                float rx = Mathf.Sqrt(Mathf.Max(0f, 1f - fy * fy));
                float a  = i * golden;
                Vector3 around = new Vector3(rx * Mathf.Cos(a), fy, rx * Mathf.Sin(a));

                for (int l = 0; l < targets.Count && used < cap; l++)
                {
                    var (limb, centre, scale) = targets[l];
                    Vector3 from = centre + around * (s.OverpressureRadius * 0.6f);
                    Vector3 dir  = (centre - from).normalized;
                    // A point under the ground or behind a wall, seen from the limb, is not
                    // somewhere air can press from - and its cast would come up through a
                    // one-sided terrain and hit the limb anyway.
                    if (Occluded(s, centre, around, s.OverpressureRadius * 0.6f)) continue;
                    if (!Physics.Raycast(from, dir, out RaycastHit hit, s.OverpressureRadius * 1.2f, s.LayerMask, QueryTriggerInteraction.Ignore)) continue;
                    if (FruitWounds.LimbOf(hit.collider)?.Pointer != limb.Pointer) continue;

                    int radius = Mathf.Max(1, Mathf.RoundToInt(s.OverpressureWoundRadius * Mathf.Lerp(0.5f, 1f, scale)));
                    // Into the limb from where the cast met it - the side facing away from the
                    // charge gets hurt less through scale, not skipped.
                    FruitWounds.Burst(limb, hit.point, dir, radius,
                                      s.OverpressureDamage * scale * s.DamageScale, scale, b.Shot, b.Rng);
                    used++;
                }
            }
            return budget - used;
        }

        private static readonly BlastInjuryProfile DefaultInjury = new BlastInjuryProfile();

        /// <summary>
        /// Overpressure from the charge itself (<see cref="ExplosionSpec.ChargeKgTNT"/>): the
        /// reflected peak pressure on each limb, from its nearest point to the charge, after
        /// cover and the shaped charge's cone. What it does then follows the pressure (thresholds
        /// in <see cref="ExplosionSpec.Injury"/>):
        /// - organs in that limb are bruised, and past twice their threshold partly torn
        ///   (<see cref="FruitBlastInjury"/>);
        /// - past the surface threshold, the side facing the charge is torn open, more and deeper
        ///   the higher it goes;
        /// - past the disruption threshold (contact range) the limb is blown apart from inside.
        /// A charge on or against a surface is a surface burst: the surface throws the wave back,
        /// worth <see cref="ExplosionSpec.SurfaceBurstFactor"/> times the charge.
        /// </summary>
        private static int BlastWave(Blast b, Vector3 forward, float quality, int budget)
        {
            var s = b.S;
            Vector3 origin = b.Origin;
            var p = s.Injury ?? DefaultInjury;

            float w = s.ChargeKgTNT * (SurfaceBurst(s, origin) ? Mathf.Max(1f, s.SurfaceBurstFactor) : 1f);
            float duration = FruitBlastInjury.DurationFactor(w);
            // Out to where the lowest threshold stops mattering (reflected ≥ 2x incident).
            float lowest = Mathf.Min(p.LungKPa, Mathf.Min(p.StomachKPa, p.SurfaceKPa)) * duration * 0.5f;
            float range = Mathf.Min(FruitBlast.RangeFor(w, lowest * 0.8f), 60f);

            int cap = Mathf.Min(budget, Mathf.Max(1, Mathf.CeilToInt(budget * Mathf.Clamp01(s.OverpressureBudgetShare))));
            int used = 0;

            var colliders = new Dictionary<IntPtr, Collider>();
            foreach (var col in Physics.OverlapSphere(origin, range, s.LayerMask, QueryTriggerInteraction.Ignore))
            {
                var limb = FruitWounds.LimbOf(col);
                if (limb == null || !b.LimbsSeen.Add(limb.Pointer)) continue;
                colliders[limb.Pointer] = col;
                b.Limbs.Add((limb, FruitWounds.BodyOf(col), limb.transform.position));
            }

            var surface = new List<(LimbEffectorReceiver limb, Vector3 centre, Vector3 toLimb, float kPa)>();
            foreach (var (limb, body, centre) in b.Limbs)
            {
                if (used >= cap) break;
                var col = colliders[limb.Pointer];
                Vector3 near = col.ClosestPoint(origin);
                float dist = Mathf.Max(0.03f, Vector3.Distance(origin, near));
                Vector3 toLimb = (centre - origin).sqrMagnitude > 1e-6f ? (centre - origin).normalized : forward;

                float cone = ConeAttenuation(s, forward, toLimb);
                if (cone < 0.01f) continue;
                float cover = Cover(b, centre, body, out var occluder, out var mat);
                float kPa = FruitBlast.ReflectedKPa(FruitBlast.IncidentKPa(dist, w) * cone * cover);
                RaiseBlast(b, centre, true, cover, occluder, mat, kPa);

                used += FruitBlastInjury.Organs(limb, kPa * s.DamageScale, duration, p, toLimb, b.Shot, b.Rng, cap - used);
                if (kPa * s.DamageScale >= p.SurfaceKPa * duration)
                    surface.Add((limb, centre, toLimb, kPa * s.DamageScale));
            }

            // Visible damage, point by point across every limb that took enough, on the side
            // facing the charge (a little past it: the wave wraps round).
            if (surface.Count > 0 && used < cap)
            {
                float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
                int points = Mathf.Max(1, Mathf.RoundToInt(s.OverpressurePoints * quality));
                for (int i = 0; i < points && used < cap; i++)
                {
                    float t  = points > 1 ? i / (float)(points - 1) : 0.5f;
                    float fy = Mathf.Lerp(-1f, 1f, t);
                    float rx = Mathf.Sqrt(Mathf.Max(0f, 1f - fy * fy));
                    float a  = i * golden;
                    Vector3 around = new Vector3(rx * Mathf.Cos(a), fy, rx * Mathf.Sin(a));

                    for (int l = 0; l < surface.Count && used < cap; l++)
                    {
                        var (limb, centre, toLimb, kPa) = surface[l];
                        if (Vector3.Dot(around, -toLimb) < -0.25f) continue;   // the far side
                        float sev = Mathf.Log10(kPa / (p.SurfaceKPa * duration));   // 0 at the threshold, 1 at 10x
                        const float reach = 0.8f;
                        if (Occluded(s, centre, around, reach)) continue;
                        Vector3 from = centre + around * reach;
                        Vector3 dir  = (centre - from).normalized;
                        if (!Physics.Raycast(from, dir, out RaycastHit hit, reach * 1.5f, s.LayerMask, QueryTriggerInteraction.Ignore)) continue;
                        if (FruitWounds.LimbOf(hit.collider)?.Pointer != limb.Pointer) continue;

                        int radius = Mathf.Clamp(Mathf.RoundToInt(s.OverpressureWoundRadius * (1f + 1.5f * sev)), 1, 8);
                        FruitWounds.Burst(limb, hit.point, dir, radius, s.OverpressureDamage * (1f + sev),
                                          Mathf.Clamp01(0.4f + 0.5f * sev), b.Shot, b.Rng);
                        used++;
                    }
                }

                // Contact range: the limb comes apart from inside, not just its skin.
                foreach (var (limb, centre, toLimb, kPa) in surface)
                {
                    if (used >= cap) break;
                    if (kPa < p.DisruptionKPa * duration) continue;
                    float sev = Mathf.Log10(kPa / (p.DisruptionKPa * duration));
                    int radius = Mathf.Clamp(Mathf.RoundToInt(3f + 6f * sev), 3, 14);
                    int bursts = sev > 0.5f ? 2 : 1;
                    for (int k = 0; k < bursts && used < cap; k++)
                    {
                        // From the charge's side, then (harder still) from the far side too.
                        Vector3 into  = k == 0 ? toLimb : -toLimb;
                        Vector3 start = centre - into * 0.4f;
                        FruitWounds.Burst(limb, start, into, radius, s.OverpressureDamage * 3f * (1f + sev), 1f, b.Shot, b.Rng);
                        used++;
                    }
                }
            }
            return budget - used;
        }

        /// <summary>On or against something solid: within 0.35 m of any non-body collider.</summary>
        private static bool SurfaceBurst(ExplosionSpec s, Vector3 origin)
        {
            foreach (var c in Physics.OverlapSphere(origin, 0.35f, s.LayerMask, QueryTriggerInteraction.Ignore))
            {
                if (c == null || FruitWounds.LimbOf(c) != null) continue;
                var rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic && c.bounds.size.sqrMagnitude < 0.25f) continue;   // the charge itself, or a pebble
                return true;
            }
            return false;
        }

        /// <summary>Anything but a body between <paramref name="from"/> and <paramref name="dist"/> metres along <paramref name="dir"/>.</summary>
        private static bool Occluded(ExplosionSpec s, Vector3 from, Vector3 dir, float dist)
        {
            var hits = Physics.RaycastAll(from, dir, dist, s.LayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
                if (hits[i].collider != null && FruitWounds.LimbOf(hits[i].collider) == null) return true;
            return false;
        }

        // ── Cover ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The fraction of the blast that reaches <paramref name="target"/>: the product of the
        /// <see cref="SurfaceMaterial.BlastTransmission"/> of every wall on the straight line to
        /// it, floored at the spec's diffraction. Bodies and small props don't count - a blast
        /// wraps round them - and nor does the target's own body.
        /// </summary>
        private static float Cover(Blast b, Vector3 target, Rigidbody targetBody, bool overpressure)
        {
            float t = Cover(b, target, targetBody, out var occluder, out var mat);
            RaiseBlast(b, target, overpressure, t, occluder, mat, 0f);
            return t;
        }

        private static void RaiseBlast(Blast b, Vector3 target, bool overpressure, float transmission,
                                       Collider occluder, SurfaceMaterial occluderMat, float peakKPa)
        {
            if (FruitBallistics.WantsBlast)
                FruitBallistics.RaiseBlast(new BlastTrace
                {
                    Spec = b.S, Owner = b.Cmd.Owner, Origin = b.Origin, Target = target, Overpressure = overpressure,
                    Transmission = transmission, Occluder = occluder, OccluderMaterial = occluderMat, PeakKPa = peakKPa,
                });
        }

        private static float Cover(Blast b, Vector3 target, Rigidbody targetBody, out Collider occluder, out SurfaceMaterial occluderMat)
        {
            var s = b.S;
            Vector3 origin = b.Origin;
            float transmission = 1f;
            occluder = null;
            occluderMat = null;

            Vector3 to = target - origin;
            float dist = to.magnitude;
            if (s.BlastOcclusion && FruitLibConfig.BlastOcclusion && dist > 0.15f)
            {
                Vector3 dir = to / dist;
                // Allocating on purpose: the NonAlloc overloads return nothing in this game.
                var hits = Physics.RaycastAll(origin + dir * 0.05f, dir, dist - 0.05f, s.LayerMask, QueryTriggerInteraction.Ignore);
                float nearest = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    var c = hits[i].collider;
                    if (c == null || FruitWounds.LimbOf(c) != null) continue;
                    var rb = c.attachedRigidbody;
                    if (rb != null && targetBody != null && rb.Pointer == targetBody.Pointer) continue;
                    Vector3 size = c.bounds.size;
                    if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < MinCoverSize) continue;

                    var m = FruitSurfaces.Resolve(c);
                    transmission *= Mathf.Clamp01(m.BlastTransmission);
                    if (hits[i].distance < nearest) { nearest = hits[i].distance; occluder = c; occluderMat = m; }
                }
                if (occluder != null) transmission = Mathf.Max(transmission, Mathf.Clamp01(s.BlastDiffraction));
            }
            return transmission;
        }

        // ── Fragments ────────────────────────────────────────────────────────────

        /// <summary>What one kind of fragment is: the spec's plain fragments, its jet, or the
        /// spall a jet blows off the back of a wall.</summary>
        private struct Shard
        {
            public bool         Jet;
            /// <summary>Wound power at <see cref="V0"/>.</summary>
            public int          Power;
            /// <summary>Real speed at full power, m/s, and sectional density, kg/m².</summary>
            public float        V0, SD;
            public WoundProfile Wound;
            /// <summary>Arc-shape speed at full power (the readable scale, not the real one).</summary>
            public float        ArcSpeed;
            /// <summary>Metres of wall still to go through at no cost.</summary>
            public float        Free;
            public int          MaxLegs;
            /// <summary>Wounds come out of the secondary budget (jet, spall, bone), not the fragment field's.</summary>
            public bool         Reserved;
            public bool         Spall;
            /// <summary>A piece of bone: throws no bone of its own.</summary>
            public bool         Bone;
        }

        /// <summary>
        /// A bone fragment: ~0.5 g, and light for its size (bone is a quarter the density of
        /// steel and irregular), so it penetrates poorly and sheds speed fast.
        /// </summary>
        private static Shard BoneShard(Blast b, float power)
        {
            const float m = 0.0005f;
            int p = Mathf.Max(1, Mathf.RoundToInt(power));
            return new Shard
            {
                Power = p, Bone = true, Reserved = true,
                V0 = Mathf.Sqrt(2f * p / (ExplosionSpec.FragPowerPerJoule * m)),
                SD = m / (1.5f * Mathf.Pow(m / 1900f, 2f / 3f)) * Mathf.Max(0f, FruitLibConfig.PenetrationScale),
                Wound = b.S.FragWound, ArcSpeed = b.S.FragSpeed, MaxLegs = 2,
            };
        }

        private static Shard PlainShard(Blast b) => new Shard
        {
            Power = b.S.FragPower, V0 = b.V0, SD = b.SD, Wound = b.S.FragWound,
            ArcSpeed = b.S.FragSpeed, MaxLegs = MaxLegs,
        };

        private static Shard JetShard(Blast b)
        {
            var s = b.S;
            int power = s.JetPower > 0 ? s.JetPower : s.FragPower;
            return new Shard
            {
                Jet = true, Power = power,
                V0 = Mathf.Sqrt(2f * power / (ExplosionSpec.FragPowerPerJoule * s.FragMassKg)),
                SD = b.SD, Wound = s.JetWound ?? s.FragWound, ArcSpeed = JetArcSpeed,
                Free = Mathf.Max(0f, s.JetPenetration), MaxLegs = MaxJetLegs, Reserved = true,
            };
        }

        /// <summary>Where a jet came out of a wall, for the spall to come off.</summary>
        private struct Scab
        {
            public Vector3 At, Axis, Normal;
            public float   PowerRatio, Thickness;
            public SurfaceMaterial Material;
        }

        private static Shard SpallShard(Blast b, float powerRatio, float massScale)
        {
            var s = b.S;
            float m = Mathf.Max(0.00005f, s.JetSpallMassGrams * massScale * 0.001f);
            int power = Mathf.Max(1, Mathf.RoundToInt(s.JetSpallPower * powerRatio * massScale));
            return new Shard
            {
                Power = power,
                V0 = Mathf.Sqrt(2f * power / (ExplosionSpec.FragPowerPerJoule * m)),
                SD = m / (1.5f * Mathf.Pow(m / 7850f, 2f / 3f)) * Mathf.Max(0f, FruitLibConfig.PenetrationScale),
                Wound = s.FragWound, ArcSpeed = s.FragSpeed, MaxLegs = MaxLegs, Reserved = true, Spall = true,
            };
        }

        private static void Fragments(Blast b, Vector3 forward, float quality)
        {
            var s = b.S;
            int rays = Mathf.Max(1, Mathf.RoundToInt(s.FragCount * quality));
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));

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
            Quaternion jitter = Quaternion.AngleAxis((float)b.Rng.NextDouble() * 360f, forward);

            var plain = PlainShard(b);
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

                bool withDebris = debris < maxDebris && r % debrisEvery == 0;
                if (withDebris) debris++;
                Fly(b, r, b.Origin + dir * 0.04f, dir, withDebris, plain);
            }
            int next = rays;

            // The jet: its own rays, spread evenly over a narrow cone round the axis, so a
            // shaped charge always punches where it points however the pattern is turned.
            int jetRays = s.JetRays > 0 && !s.IsFullSphere ? s.JetRays : 0;
            float jetHalf = Mathf.Clamp(s.JetConeDeg * 0.5f, 0f, 45f) * Mathf.Deg2Rad;
            b.SpallQuality = quality;
            for (int j = 0; j < jetRays; j++)
            {
                float t  = jetRays > 1 ? j / (float)(jetRays - 1) : 0f;
                float az = j * golden;
                float th = Mathf.Sqrt(t) * jetHalf;
                Vector3 dir = (jitter * (coneRot * new Vector3(Mathf.Sin(th) * Mathf.Cos(az), Mathf.Sin(th) * Mathf.Sin(az), Mathf.Cos(th)))).normalized;
                Fly(b, next++, b.Origin + dir * 0.04f, dir, withDebris: false, JetShard(b));
            }

            // Spall the jet blew off the back of each wall it went through. Queued rather than
            // flown from inside the jet's own flight, which is still using this blast's state.
            for (int i = 0; i < b.Spall.Count; i++)
                Throw(b, b.Spall[i], ref next);
            b.Spall.Clear();

            // Bone blown out of exit wounds, by fragments, jet and spall alike. Last, and it
            // throws no bone of its own, so this ends.
            for (int i = 0; i < b.Bones.Count; i++)
            {
                var (at, dir, power) = b.Bones[i];
                Fly(b, next++, at + dir * (FragRadius + 0.01f), dir, withDebris: false, BoneShard(b, power));
            }
            b.Bones.Clear();
        }

        /// <summary>
        /// Behind-armour debris. The back face fails in tension round the exit hole and comes
        /// away as a ragged patch (scabbing): its radius is the hole plus the shear cone through
        /// the wall's thickness, and its edge is noisy - a few lobes plus per-chunk jitter. Each
        /// chunk leaves from where it broke off: from the middle fast and close to the jet line,
        /// from the rim slower and splayed out towards <see cref="ExplosionSpec.JetSpallConeDeg"/>.
        /// Chunk sizes are skewed small, as broken concrete is.
        /// </summary>
        private static void Throw(Blast b, Scab sc, ref int next)
        {
            var s = b.S;
            var rng = b.Rng;
            float matSpall = sc.Material != null ? Mathf.Max(0f, sc.Material.Spall) : 1f;
            int count = Mathf.RoundToInt(s.JetSpallCount * matSpall * b.SpallQuality);
            if (count <= 0) return;

            // The patch lies on the back face; leave along its normal if the far side had one
            // that faces out, else square to the jet.
            Vector3 n = Vector3.Dot(sc.Normal, sc.Axis) > 0.05f ? sc.Normal.normalized : sc.Axis;
            Vector3 t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
            Vector3 t2 = Vector3.Cross(n, t1);

            float radius = Mathf.Clamp(0.03f + 0.8f * sc.Thickness, 0.04f, 0.6f) * Mathf.Max(0.05f, s.JetSpallCraterScale);
            float half = Mathf.Clamp(s.JetSpallConeDeg * 0.5f, 1f, 89f) * Mathf.Deg2Rad;

            // The ragged edge: two lobed harmonics at random phase, so no two scabs match.
            float ph1 = (float)rng.NextDouble() * Mathf.PI * 2f, ph2 = (float)rng.NextDouble() * Mathf.PI * 2f;
            int lobes1 = 3 + rng.Next(3), lobes2 = 7 + rng.Next(5);

            for (int i = 0; i < count; i++)
            {
                float az   = (float)rng.NextDouble() * Mathf.PI * 2f;
                float edge = 1f + 0.25f * Mathf.Sin(lobes1 * az + ph1) + 0.12f * Mathf.Sin(lobes2 * az + ph2);
                float f    = Mathf.Sqrt((float)rng.NextDouble()) * edge * (0.85f + 0.3f * (float)rng.NextDouble());
                Vector3 outward = t1 * Mathf.Cos(az) + t2 * Mathf.Sin(az);
                Vector3 from = sc.At + outward * (radius * f);

                // Splay with distance from the hole, plus a tumble of its own.
                float th = Mathf.Clamp01(f) * half + ((float)rng.NextDouble() - 0.5f) * 0.35f * half;
                Vector3 dir = (sc.Axis * Mathf.Cos(th) + outward * Mathf.Sin(th)).normalized;
                if (Vector3.Dot(dir, n) < 0.1f) dir = (Vector3.ProjectOnPlane(dir, n).normalized + n * 0.15f).normalized;

                // Middle chunks carry the most; mostly small pieces, the odd big one.
                float u = (float)rng.NextDouble();
                float massScale  = 0.35f + 2.6f * u * u * u;
                float powerScale = Mathf.Lerp(1f, 0.3f, Mathf.Clamp01(f)) * (0.6f + 0.8f * (float)rng.NextDouble());

                var shard = SpallShard(b, sc.PowerRatio * powerScale, massScale);
                Fly(b, next++, from + n * (FragRadius + 0.01f), dir, withDebris: false, shard);
            }
        }

        /// <summary>One fragment, leg by leg, until it stops, is spent, or runs out of legs.</summary>
        private static void Fly(Blast b, int fragment, Vector3 p0, Vector3 dir, bool withDebris, Shard k)
        {
            var s = b.S;
            float gy = Physics.gravity.y;
            float speed = k.V0;               // physical, m/s
            float timeLeft = s.FragMaxTime;
            int bounces = 0;
            bool walkedBody = false;
            b.Passed.Clear();

            for (int leg = 0; leg < k.MaxLegs; leg++)
            {
                // The arc keeps its readable scale, shrunk with the speed lost so far. A jet's
                // is fast enough to be flat.
                Vector3 arcVel = dir * (k.ArcSpeed * (speed / k.V0));
                float flight = BallisticGroundTime(gy, arcVel.y, p0.y - b.GroundY, timeLeft);

                bool didHit = SweepArc(b, p0, arcVel, gy, flight, out RaycastHit hit, out float hitTime);

                if (withDebris && leg == 0)
                    FruitBallistics.RaiseDebris(s, p0, arcVel, didHit ? hitTime : flight);

                var trace = new FragmentTrace
                {
                    Spec = s, Owner = b.Cmd.Owner, Cosmetic = b.Cosmetic, Fragment = fragment, Leg = leg, Jet = k.Jet, Spall = k.Spall, Bone = k.Bone,
                    Start = p0, ArcVelocity = arcVel, Time = didHit ? hitTime : flight,
                    End = didHit ? hit.point : Arc(p0, arcVel, gy, flight), EndedBy = FragmentEnd.Spent,
                };

                if (!didHit)
                {
                    Emit(b, ref trace);
                    return;
                }

                // Speed at the hit: power falls as exp(-falloff·d), so speed at half that rate.
                float travelled = Vector3.Distance(p0, hit.point);
                float speedIn = speed * Mathf.Exp(-0.5f * s.FragPowerFalloff * travelled);
                Vector3 v = arcVel + new Vector3(0f, gy * hitTime, 0f);
                Vector3 travel = v.sqrMagnitude > 1e-8f ? v.normalized : dir;

                trace.Collider = hit.collider;
                trace.Normal   = hit.normal;
                trace.SpeedIn  = speedIn;
                timeLeft -= hitTime;

                if (leg == 0 && Vector3.Distance(b.Origin, hit.point) < 0.02f)
                {
                    // Against the charge's own casing or the face it sits on.
                    trace.EndedBy = FragmentEnd.Stopped;
                    Emit(b, ref trace);
                    return;
                }

                float ratio = speedIn / k.V0;
                if (ratio * ratio < KillPowerRatio)
                {
                    trace.EndedBy = FragmentEnd.TooWeak;
                    Emit(b, ref trace);
                    return;
                }

                var limb = FruitWounds.LimbOf(hit.collider);
                bool goesOn = limb != null
                    ? HitLimb(b, ref k, limb, hit, travel, speedIn, ref walkedBody, ref trace, out p0, out dir, out speed)
                    : HitSurface(b, ref k, hit, travel, speedIn, ref bounces, ref trace, out p0, out dir, out speed);

                Emit(b, ref trace);
                if (!goesOn || timeLeft <= 0.001f) return;
            }
        }

        private static bool HitLimb(Blast b, ref Shard k, LimbEffectorReceiver limb, RaycastHit hit, Vector3 dir, float speedIn,
                                    ref bool walkedBody, ref FragmentTrace trace,
                                    out Vector3 next, out Vector3 nextDir, out float nextSpeed)
        {
            var s = b.S;
            next = hit.point; nextDir = dir; nextSpeed = 0f;
            trace.Incidence = Vector3.Angle(-dir, hit.normal);

            var rb = FruitWounds.BodyOf(hit.collider);
            if (b.Cosmetic)
            {
                // What the wound walk would have left is the host's to know: draw it stopping.
                trace.EndedBy = FragmentEnd.Lodged;
                return false;
            }

            float ratio = speedIn / k.V0;
            if (rb != null && s.FragImpulse > 0f) AddImpulse(b, rb, dir * (s.FragImpulse * ratio));

            int power = Mathf.RoundToInt(k.Power * s.DamageScale * ratio * ratio);
            if (power < 1)
            {
                trace.EndedBy = FragmentEnd.TooWeak;
                return false;
            }

            ref int budget = ref (k.Reserved ? ref b.SecondaryBudget : ref b.Budget);
            if (budget <= 0)
            {
                // No wound, but the body is no shield either: the fragment goes on with what
                // crossing that much soft tissue would have cost it. Stopping it here made the
                // first body in the way soak up everything past the budget.
                trace.EndedBy = FragmentEnd.OverBudget;
                if (!FruitSurfaces.FarSide(hit.collider, hit.point, dir, 1.5f, out Vector3 far, out float depth))
                    return false;
                int left = power - Mathf.RoundToInt(depth * SoftTissueCostPerMetre(k.Wound));
                float vLeft = left > 0 ? speedIn * Mathf.Sqrt(left / (float)power) : 0f;
                if (vLeft / k.V0 * (vLeft / k.V0) < KillPowerRatio) return false;

                if (rb != null) b.Passed.Add(rb.Pointer);
                trace.Exit     = far;
                trace.SpeedOut = vLeft;
                next      = far + dir * (FragRadius + 0.01f);
                nextSpeed = vLeft;
                return true;
            }

            var res = FruitWounds.Channel(limb, rb, hit.point, hit.normal, dir, power, k.Power,
                                          k.Wound, b.Shot, b.Rng, firstBody: !walkedBody, cosmetic: false);
            if (rb != null) b.Passed.Add(rb.Pointer);

            if (!res.Touched)
            {
                // Through a hole, or the collider is wider than the flesh: carry on unchanged.
                trace.EndedBy   = FragmentEnd.PassedThrough;
                trace.Exit      = hit.point;
                trace.SpeedOut  = speedIn;
                next = hit.point + dir * 0.01f;
                nextSpeed = speedIn;
                return true;
            }

            budget--;
            walkedBody = true;
            FruitBallistics.RaiseWounded(new WoundInfo
            {
                Limb = limb.gameObject, Entry = hit.point, Exit = res.Exit, Direction = dir,
                PowerIn = power, PowerOut = res.PowerOut, Exited = res.Exited, Steps = res.Steps,
            });

            // Through bone and out: some of it comes along.
            if (res.Exited && !k.Bone && s.BoneFragments > 0 && res.HardSteps > 0)
                QueueBone(b, res, dir, power);

            // Power tracks energy, so what is left out of the far side sets the speed.
            float vOut = speedIn * Mathf.Sqrt(Mathf.Clamp01(res.PowerOut / (float)power));
            float outRatio = vOut / k.V0;
            if (!res.Exited || outRatio * outRatio < KillPowerRatio)
            {
                trace.EndedBy = FragmentEnd.Lodged;
                return false;
            }

            trace.EndedBy  = FragmentEnd.PassedThrough;
            trace.Exit     = res.Exit;
            trace.SpeedOut = vOut;
            nextDir   = k.Jet ? dir : FruitProjectiles.Deflect(b.Rng, dir, LimbExitDeflect);
            next      = res.Exit + nextDir * 0.03f;
            nextSpeed = vOut;
            return true;
        }

        /// <summary>
        /// Secondary fragments of bone out of an exit wound: one per two bone steps crossed, up
        /// to the spec's limit, in a cone along the path. Their power follows how hard the hit
        /// was against a plain fragment at the charge, so a jet through a femur throws far
        /// harder pieces than a spent fragment through a rib.
        /// </summary>
        private static void QueueBone(Blast b, FruitWounds.Result res, Vector3 dir, int powerIn)
        {
            var s = b.S;
            int count = Mathf.Clamp((res.HardSteps + 1) / 2, 1, s.BoneFragments);
            float hitScale = Mathf.Clamp(powerIn / Mathf.Max(1f, s.FragPower * s.DamageScale), 0.2f, 3f);
            float half = Mathf.Clamp(s.BoneFragmentConeDeg * 0.5f, 1f, 89f);
            for (int i = 0; i < count; i++)
            {
                Vector3 d = FruitProjectiles.Deflect(b.Rng, dir, half);
                float power = s.BoneFragmentPower * hitScale * (0.5f + (float)b.Rng.NextDouble());
                b.Bones.Add((res.Exit, d, power));
            }
        }

        /// <summary>
        /// Rough power a fragment spends per metre of soft tissue, for passing through a body
        /// without walking it: ~100 per voxel (the probe's muscle figure), a voxel ~23 mm, times
        /// the crush sphere plus the spread neighbours it takes on average.
        /// </summary>
        private static float SoftTissueCostPerMetre(WoundProfile w)
        {
            int crush = FruitWounds.Sphere(w != null ? w.CrushRadius : 0).Count;
            float spread = w != null ? w.SpreadChance * 3f : 0f;
            return 100f * (crush + spread) / 0.023f;
        }

        /// <summary>Ricochet if the angle says so, else through if it has the energy, else stop -
        /// the rules rounds follow, with the fragment's own numbers.</summary>
        private static bool HitSurface(Blast b, ref Shard k, RaycastHit hit, Vector3 dir, float speedIn, ref int bounces,
                                       ref FragmentTrace trace, out Vector3 next, out Vector3 nextDir, out float nextSpeed)
        {
            var s = b.S;
            next = hit.point; nextDir = dir; nextSpeed = 0f;

            var m = FruitSurfaces.Resolve(hit.collider);
            float incidence = Vector3.Angle(-dir, hit.normal);
            trace.Material  = m;
            trace.Incidence = incidence;

            // Ricochet. A jet with penetration left does not glance off anything.
            float chance = m.RicochetChance(s.FragRicochetAngle, incidence, RicochetBand);
            if (k.Free <= 0f && bounces < s.FragMaxBounces && chance > 0f && b.Rng.NextDouble() < chance)
            {
                bounces++;
                float keep = Mathf.Sqrt(Mathf.Clamp01(1f - s.FragRicochetEnergyLoss * m.RicochetLossScale));
                nextSpeed = speedIn * keep;
                nextDir   = FruitProjectiles.KeepOff(
                    FruitProjectiles.Deflect(b.Rng, m.Bounce(dir, hit.normal).normalized, s.FragRicochetScatter), hit.normal);
                next      = hit.point + hit.normal * (FragRadius + 0.01f);
                Push(b, k, hit, dir * speedIn - nextDir * nextSpeed);

                trace.EndedBy  = FragmentEnd.Ricocheted;
                trace.SpeedOut = nextSpeed;
                return nextSpeed / k.V0 * (nextSpeed / k.V0) >= KillPowerRatio;
            }

            // Through. The free part (a jet's) comes first and costs nothing; Poncelet
            // prices whatever of the wall is left past it.
            if (FruitLibConfig.WallPenetration && (k.SD > 0f || k.Free > 0f) && m.Penetrable)
            {
                float reach = k.Free + Mathf.Min(m.Depth(k.SD, speedIn), m.MaxThickness);
                if (reach >= 0.002f &&
                    FruitSurfaces.FarSide(hit.collider, hit.point, dir, reach, out Vector3 exit, out float thickness, out Vector3 exitNormal))
                {
                    float paid = Mathf.Max(0f, thickness - k.Free);
                    float vOut = paid > 0f ? m.ExitSpeed(k.SD, speedIn, paid) : speedIn;
                    float outRatio = vOut / k.V0;
                    if (vOut > 0f && outRatio * outRatio >= KillPowerRatio)
                    {
                        // A jet that got through blows the back of the wall out - once per wall.
                        if (k.Jet && b.S.JetSpallCount > 0 && b.Spalled.Add(hit.collider.GetInstanceID()))
                            b.Spall.Add(new Scab
                            {
                                At = exit, Axis = dir, Normal = exitNormal, PowerRatio = outRatio * outRatio,
                                Thickness = thickness, Material = m,
                            });

                        k.Free = Mathf.Max(0f, k.Free - thickness);
                        float lost = 1f - vOut / Mathf.Max(0.01f, speedIn);
                        nextDir   = FruitProjectiles.Deflect(b.Rng, dir, m.ExitScatter * lost);
                        next      = exit + dir * (FragRadius + 0.01f);
                        nextSpeed = vOut;
                        Push(b, k, hit, dir * (speedIn - vOut));

                        trace.EndedBy   = FragmentEnd.Penetrated;
                        trace.Exit      = exit;
                        trace.Thickness = thickness;
                        trace.SpeedOut  = vOut;
                        return true;
                    }
                }
            }

            // Stopped.
            Push(b, k, hit, dir * speedIn);
            trace.EndedBy = FragmentEnd.Stopped;
            return false;
        }

        /// <summary>The spec's FragImpulse for a fragment stopping dead at full speed, scaled by
        /// the velocity it actually lost there.</summary>
        private static void Push(Blast b, in Shard k, RaycastHit hit, Vector3 lostVelocity)
        {
            if (b.Cosmetic || b.S.FragImpulse <= 0f) return;
            var rb = FruitWounds.BodyOf(hit.collider);
            if (rb == null || rb.isKinematic) return;
            AddImpulse(b, rb, lostVelocity * (b.S.FragImpulse / Mathf.Max(1f, k.V0)));
        }

        private static void Emit(Blast b, ref FragmentTrace t)
        {
            if (b.Trace) FruitBallistics.RaiseFragment(t);
        }

        private static Vector3 Arc(Vector3 p0, Vector3 vel, float gy, float t)
            => p0 + vel * t + new Vector3(0f, 0.5f * gy * t * t, 0f);

        /// <summary>
        /// Sweeps the arc in straight segments. Bodies the fragment has already been through
        /// are looked past: the sweep steps over them and carries on down the same segment.
        /// </summary>
        private static bool SweepArc(Blast b, Vector3 p0, Vector3 vel, float gy, float flight,
                                     out RaycastHit hit, out float hitTime)
        {
            var s = b.S;
            hit = default; hitTime = flight;
            if (flight <= 0f) return false;
            Vector3 landing = Arc(p0, vel, gy, flight);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(p0, landing) / 0.75f), 2, Mathf.Max(2, s.ArcSteps));

            Vector3 prev = p0;
            float prevT = 0f;
            for (int i = 1; i <= steps; i++)
            {
                float ft = flight * (i / (float)steps);
                Vector3 pt = Arc(p0, vel, gy, ft);
                Vector3 seg = pt - prev;
                float len = seg.magnitude;
                if (len > 0f)
                {
                    Vector3 sdir = seg / len;
                    Vector3 from = prev;
                    float done = 0f;
                    for (int guard = 0; guard < 4 && done < len; guard++)
                    {
                        if (!Physics.SphereCast(from, FragRadius, sdir, out hit, len - done, s.LayerMask, QueryTriggerInteraction.Ignore))
                            break;
                        if (!Passed(b, hit.collider))
                        {
                            hitTime = prevT + (ft - prevT) * Mathf.Clamp01((done + hit.distance) / len);
                            return true;
                        }
                        float skip = hit.distance + 0.05f;
                        from += sdir * skip;
                        done += skip;
                    }
                }
                prev = pt;
                prevT = ft;
            }
            hit = default;
            return false;
        }

        private static bool Passed(Blast b, Collider c)
        {
            if (b.Passed.Count == 0) return false;
            var rb = FruitWounds.BodyOf(c);
            return rb != null && b.Passed.Contains(rb.Pointer);
        }

        // ── Helpers (BombsAway) ─────────────────────────────────────────────────

        private static void AddImpulse(Blast b, Rigidbody rb, Vector3 dv)
        {
            if (b.Impulses.TryGetValue(rb.Pointer, out var e)) b.Impulses[rb.Pointer] = (rb, e.dv + dv);
            else b.Impulses[rb.Pointer] = (rb, dv);
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
