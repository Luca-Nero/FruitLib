using System;
using System.Collections.Generic;
using Il2CppData.CustomTypes.LimitedValue;
using Il2CppEffectors;
using Il2CppEffectors.ReceiveMethods.Index;
using Il2CppEffectors.Types;
using Il2CppLVA.Limbs;
using Il2CppLVA.Limbs.Variants.Human;
using Il2CppLVA.Organs;
using Il2CppLVA.Organs.EffectorsPerception.Collectors;
using MelonLoader;
using Unity.Mathematics;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// How strongly a blast wave hurts what, as reflected peak overpressure in kPa for a long
    /// (several ms) positive phase. Small charges have short phases, which the body tolerates
    /// better; <see cref="FruitBlastInjury"/> raises every threshold for that.
    ///
    /// The organ figures follow the primary-blast literature (Bowen/Bass): the gas-filled lung
    /// is the most vulnerable organ, then the gut; solid organs and the heart need much more.
    /// The game has no eardrum or bowel organ, so the stomach stands in for the gut.
    /// </summary>
    public sealed class BlastInjuryProfile
    {
        /// <summary>Lung contusion begins. Twice this is serious; four times tears it apart.</summary>
        public float LungKPa    = 150f;
        public float StomachKPa = 250f;
        public float BrainKPa   = 450f;
        public float HeartKPa   = 500f;
        public float LiverKPa   = 700f;
        /// <summary>Visible tissue damage on the side facing the charge begins.</summary>
        public float SurfaceKPa = 1200f;
        /// <summary>The limb itself comes apart: contact and near-contact range.</summary>
        public float DisruptionKPa = 6000f;

        public BlastInjuryProfile Clone() => (BlastInjuryProfile)MemberwiseClone();
    }

    /// <summary>
    /// Primary blast injury for <see cref="ExplosionSpec.ChargeKgTNT"/> charges.
    ///
    /// The game's organs are regions of their limb's voxels; there is no organ-damage call and
    /// no lung failure. What the game does have is per-voxel damage progress that organs
    /// collect, and pain, agony and cognition that follow organ damage. So a blast hurts an
    /// organ the way a contusion would: weak damage signals over a share of its voxels, which
    /// lower the organ without carving it (strong enough, and some voxels go - a rupture).
    ///
    /// Organs are found through the concrete limb types (Spine: lungs, heart; Head: brain;
    /// Pelvis: stomach, liver), and each organ's voxels once, by scanning round its centre with
    /// the limb's own voxel-to-organ lookup, then cached.
    /// </summary>
    internal static class FruitBlastInjury
    {
        private const string Tag = "[FruitBlast]";

        /// <summary>Per-voxel influence for a bruised voxel at the threshold, and at worst - under
        /// the 100 a voxel has, so contusion never removes one on its own.</summary>
        private const float ContusionMin = 30f, ContusionMax = 90f;
        private const float Rupture = 400f;

        private static readonly Dictionary<IntPtr, int3[]> _organVoxels = new Dictionary<IntPtr, int3[]>();
        private static bool _warnedSpace;

        internal static void ResetForScene() => _organVoxels.Clear();

        /// <summary>How much tougher the body is against a short blast: the duration of a charge's
        /// positive phase scales with W^⅓ (~1.8 ms per kg^⅓), and the thresholds were set for ~3 ms.</summary>
        internal static float DurationFactor(float kgTnt)
        {
            float ms = 1.8f * Mathf.Pow(Mathf.Max(1e-4f, kgTnt), 1f / 3f);
            return Mathf.Sqrt(Mathf.Max(1f, 3f / ms));
        }

        /// <summary>
        /// The organs of <paramref name="limb"/> hit by reflected overpressure <paramref name="kPa"/>.
        /// Returns how many organs were hurt (each is one wound off the budget).
        /// </summary>
        internal static int Organs(LimbEffectorReceiver limb, float kPa, float durationFactor, BlastInjuryProfile p,
                                   Vector3 fromCharge, EffectorHit shot, System.Random rng, int budget)
        {
            if (budget <= 0 || p == null) return 0;
            AbstractLimb body;
            try { body = limb.m_limbReferences?.Limb; }
            catch { return 0; }
            if (body == null) return 0;

            int used = 0;
            var spine = body.TryCast<Spine>();
            if (spine != null)
            {
                used += Organ(limb, spine.m_leftLung,  "left lung",  kPa, p.LungKPa  * durationFactor, fromCharge, shot, rng, budget - used);
                used += Organ(limb, spine.m_rightLung, "right lung", kPa, p.LungKPa  * durationFactor, fromCharge, shot, rng, budget - used);
                used += Organ(limb, spine.m_heart,     "heart",      kPa, p.HeartKPa * durationFactor, fromCharge, shot, rng, budget - used);
                return used;
            }
            var pelvis = body.TryCast<Pelvis>();
            if (pelvis != null)
            {
                used += Organ(limb, pelvis.m_stomach, "stomach", kPa, p.StomachKPa * durationFactor, fromCharge, shot, rng, budget - used);
                used += Organ(limb, pelvis.m_liver,   "liver",   kPa, p.LiverKPa   * durationFactor, fromCharge, shot, rng, budget - used);
                return used;
            }
            var head = body.TryCast<Head>();
            if (head != null)
                used += Organ(limb, head.m_brain, "brain", kPa, p.BrainKPa * durationFactor, fromCharge, shot, rng, budget - used);
            return used;
        }

        private static int Organ(LimbEffectorReceiver limb, AbstractOrgan organ, string name, float kPa, float threshold,
                                 Vector3 fromCharge, EffectorHit shot, System.Random rng, int budget)
        {
            if (organ == null || budget <= 0 || kPa < threshold) return 0;

            var voxels = VoxelsOf(limb, organ, name);
            if (voxels == null || voxels.Length == 0) return 0;

            // s: 0 at the threshold, 1 at twice it, 2 at four times.
            float s = Mathf.Log(kPa / threshold, 2f);
            float bruised   = Mathf.Clamp01(0.25f + 0.35f * s);
            float ruptured  = Mathf.Clamp01((s - 1f) * 0.3f);
            float contusion = -Mathf.Lerp(ContusionMin, ContusionMax, Mathf.Clamp01(s / 2f));

            var list = new IndexEffectorSignalsList(voxels.Length, false);
            int hurt = 0, gone = 0;
            foreach (var v in voxels)
            {
                double roll = rng.NextDouble();
                if (roll < ruptured)               { list.Add(new IndexEffectorSignal(v, -Rupture, InfluenceProcessType.Sum)); gone++; }
                else if (roll < ruptured + bruised) { list.Add(new IndexEffectorSignal(v, contusion, InfluenceProcessType.Sum)); hurt++; }
            }
            if (list.Count == 0) { list.Dispose(); return 0; }

            float before = Durability(organ);
            var handler = new IndexEffectorSignalsHandler<Destruction>(list, new IndexEffectorDescription(fromCharge, shot));
            try { limb.Receive(handler); }
            catch (Exception e) { MelonLogger.Warning($"{Tag} {name} injury failed: {e.Message}"); return 0; }
            finally { handler.Dispose(); }

            if (FruitBallisticsProbe.Enabled)
                MelonLogger.Msg($"{Tag} {name}: {kPa:0} kPa vs {threshold:0} (x{kPa / threshold:0.0}) -> " +
                                $"{hurt} of {voxels.Length} voxels bruised at {contusion:0}, {gone} ruptured; " +
                                $"durability {before:0.#} -> {Durability(organ):0.#}");
            return 1;
        }

        private static float Durability(AbstractOrgan organ)
        {
            try { return organ.References.Durability.Cast<IReadOnlyLimitedValueReadout>().Value; }
            catch { return float.NaN; }
        }

        /// <summary>
        /// An organ's voxels in its limb's index space, found once: scan outward from the organ's
        /// centre, asking the limb which organ owns each index, until as many as the organ has
        /// are found (or the scan is clearly past it). Cached until the scene changes.
        /// </summary>
        private static int3[] VoxelsOf(LimbEffectorReceiver limb, AbstractOrgan organ, string name)
        {
            if (_organVoxels.TryGetValue(organ.Pointer, out var cached)) return cached;

            var found = new List<int3>();
            try
            {
                var shape = limb.m_limbReferences.ShapeDataHandler;
                var oshape = organ.References.ShapeDataHandler;
                int want = oshape.VoxelsCount;
                int3 c = oshape.AABBCenter;
                IntPtr me = organ.Pointer;

                // Organs are not spheres (a lung is long): scan shells out to well past the
                // radius of a ball of the same volume.
                int rBall = Mathf.CeilToInt(Mathf.Pow(3f * Mathf.Max(1, want) / (4f * Mathf.PI), 1f / 3f));
                int rMax = Mathf.Clamp(rBall * 3, 4, 24);
                for (int r = 0; r <= rMax && found.Count < want; r++)
                {
                    for (int x = -r; x <= r; x++)
                    for (int y = -r; y <= r; y++)
                    for (int z = -r; z <= r; z++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) != r) continue;   // this shell only
                        var idx = new int3(c.x + x, c.y + y, c.z + z);
                        if (shape.TryGetOrganByVoxelIndex(idx, out AbstractOrgan owner) && owner != null && owner.Pointer == me)
                            found.Add(idx);
                    }
                    // Past the ball's radius with nothing at all: this is not the right space.
                    if (r > rBall + 2 && found.Count == 0) break;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"{Tag} {name}: voxel lookup failed: {e.Message}"); }

            if (found.Count == 0 && !_warnedSpace)
            {
                _warnedSpace = true;
                MelonLogger.Warning($"{Tag} found no voxels for the {name} round its centre; organ blast injury is off for such organs");
            }
            var arr = found.ToArray();
            _organVoxels[organ.Pointer] = arr;
            return arr;
        }
    }
}
