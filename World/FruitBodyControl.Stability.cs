using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppLVA.Limbs.Systems;
using Il2CppLVA.Puppeteers.Variants.Humanoid;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        // Native values remain the authority. Restore our previous write before a native sync,
        // then scale its newly computed drive. Never accumulate multipliers or freeze injury values.
        private sealed class WalkDrive
        {
            public ConfigurableJoint Joint;
            public JointDrive Native, Applied;
            public bool Written;
            public float Weight, Spring;
        }
        private sealed class WalkSupport
        {
            public FruitBody Body;
            public IntPtr Creature;
            public readonly Dictionary<IntPtr, WalkDrive> Drives = new Dictionary<IntPtr, WalkDrive>();
            public FootFrictionControl Friction;
            public PhysicsMaterial Stance, Swing;
            public bool Traction;
            public IntPtr LeftFoot, RightFoot;
        }
        private static readonly Dictionary<int, WalkSupport> _walkSupports = new Dictionary<int, WalkSupport>();
        private static readonly Dictionary<IntPtr, WalkDrive> _walkDrives = new Dictionary<IntPtr, WalkDrive>();
        private static readonly Dictionary<IntPtr, WalkSupport> _walkFeet = new Dictionary<IntPtr, WalkSupport>();
        private static readonly List<int> _supportDone = new List<int>();
        // Raw walks stay genuinely native unless the diagnostic harness explicitly opts in.
        internal static bool ProbeLocomotion = false;

        private static void RestoreDrive(WalkDrive d)
        {
            if (!d.Written || d.Joint == null || d.Joint.WasCollected) return;
            var current = d.Joint.slerpDrive;
            // Another subsystem may have written since us. Preserve that value.
            if (current.positionSpring == d.Applied.positionSpring && current.positionDamper == d.Applied.positionDamper &&
                current.maximumForce == d.Applied.maximumForce) d.Joint.slerpDrive = d.Native;
            d.Written = false;
        }
        private static void ApplyDrive(WalkDrive d)
        {
            if (d.Joint == null || d.Joint.WasCollected || d.Joint.rotationDriveMode != RotationDriveMode.Slerp) return;
            d.Native = d.Joint.slerpDrive;
            if (d.Weight <= 0.001f || d.Spring <= 1f) return;
            float spring = Mathf.Lerp(1f, d.Spring, d.Weight);
            var drive = d.Native;
            drive.positionSpring *= spring;
            drive.positionDamper *= Mathf.Sqrt(spring);
            // Zero damping stays zero; the separate pelvis-damping trial did not justify a default.
            d.Applied = drive; d.Joint.slerpDrive = drive; d.Written = true;
        }
        private static PhysicsMaterial FootMaterial(PhysicsMaterial source, string suffix)
        {
            if (source == null) return null;
            var material = UnityEngine.Object.Instantiate(source);
            material.name = "FruitWalk_" + suffix;
            // Bound planted grip to the surface; the native low-friction swing keeps its coefficients.
            material.frictionCombine = PhysicsMaterialCombine.Minimum;
            return material;
        }
        private static WalkSupport SupportFor(FruitBody b)
        {
            if (_walkSupports.TryGetValue(b.Id, out var s))
            {
                if (s.Creature == b.CreaturePtr) return s;
                ReleaseSupport(b.Id);
            }
            s = new WalkSupport { Body = b, Creature = b.CreaturePtr, Friction = b.Bind.Refs?.FootFrictionControl };
            _walkSupports[b.Id] = s;
            for (int i = 0; i < FruitBodies.LimbCount; i++)
            {
                var limb = b.Limbs[i]; if (!limb.Present || limb.Native == null) continue;
                ConfigurableJoint joint = null; IntPtr pointer = IntPtr.Zero;
                if (i == FruitBodies.Pelvis)
                {
                    var face = limb.Native.EntityInternalSystemsPublic?.GetSystemExternalInteraction<IPelvisMuscleForceExternalInteraction>(false);
                    if (face != null) { var m = new PelvisMuscleForce(face.Pointer); pointer = m.Pointer; joint = m.m_jointProvider?.m_joint; }
                }
                else
                {
                    var face = limb.Native.EntityInternalSystemsPublic?.GetSystemExternalInteraction<IDefaultMuscleForceInteraction>(false);
                    if (face != null) { var m = new DefaultMuscleForce(face.Pointer); pointer = m.Pointer; joint = m.m_muscleSlerpDriveSetter?.m_jointProvider?.m_joint; }
                }
                if (joint == null) continue;
                var d = new WalkDrive { Joint = joint };
                s.Drives[pointer] = d; _walkDrives[pointer] = d;
                // Store the limb index in a separate lookup so tuning changes can take effect live.
                _driveLimbs[pointer] = i;
            }
            if (s.Friction != null)
            {
                s.Stance = FootMaterial(s.Friction.m_defaultFriction, "stance");
                s.Swing = FootMaterial(s.Friction.m_zeroFriction, "swing");
                if (s.Friction.m_leftFootPhysics != null) { s.LeftFoot = s.Friction.m_leftFootPhysics.Pointer; _walkFeet[s.LeftFoot] = s; }
                if (s.Friction.m_rightFootPhysics != null) { s.RightFoot = s.Friction.m_rightFootPhysics.Pointer; _walkFeet[s.RightFoot] = s; }
            }
            _walkSupports[b.Id] = s;
            return s;
        }
        private static readonly Dictionary<IntPtr, int> _driveLimbs = new Dictionary<IntPtr, int>();
        private static void TickWalkSupport()
        {
            _supportDone.Clear();
            foreach (var pair in _walkSupports)
                if (!_orders.TryGetValue(pair.Key, out var order) || !order.Body.Alive || !order.Body.Conscious ||
                    !Upright(order.Body) || order.Body.CreaturePtr != pair.Value.Creature || FruitPose.IsHolding(order.Body)) _supportDone.Add(pair.Key);
            foreach (int id in _supportDone) ReleaseSupport(id);
            foreach (var pair in _orders)
            {
                var o = pair.Value; var b = o.Body;
                bool active = (o.Requested || o.Turn) && !o.Paused && Upright(b) && b.Alive && b.Conscious &&
                    (!o.Raw || ProbeLocomotion) && !FruitPose.IsHolding(b);
                if (!active || (!FruitLibConfig.WalkStability && !FruitLibConfig.FootTraction)) { ReleaseSupport(b.Id); continue; }
                try
                {
                    var s = SupportFor(b);
                    bool healthy = b.Blood >= 95f && b.Pain < 5f && b.LimbsMissing == 0;
                    bool traction = FruitLibConfig.FootTraction && healthy;
                    if (s.Traction != traction)
                    {
                        s.Traction = traction;
                        s.Friction?.SetDefaultFriction();
                    }
                    // Keep native weakness and fall recovery completely untouched on injured bodies.
                    foreach (var kv in s.Drives)
                    {
                        var d = kv.Value; RestoreDrive(d);
                        int limb = _driveLimbs[kv.Key];
                        float gain = limb == FruitBodies.Pelvis ? FruitLibConfig.WalkPelvisSpring :
                            limb == FruitBodies.Torso || limb == FruitBodies.Head ? FruitLibConfig.WalkTorsoSpring :
                            limb < FruitBodies.Torso ? FruitLibConfig.WalkLegSpring : 1f;
                        d.Spring = Mathf.Clamp(gain, 1f, 2f);
                        d.Weight = healthy ? Mathf.MoveTowards(d.Weight, FruitLibConfig.WalkStability ? 1f : 0f, Time.fixedDeltaTime / 0.3f) : 0f;
                        ApplyDrive(d);
                    }
                }
                catch (Exception e) { FruitBodies.ReportOnce("walking support", e); ReleaseSupport(b.Id); }
            }
        }
        private static void ReleaseSupport(int id)
        {
            if (!_walkSupports.TryGetValue(id, out var s)) return;
            _walkSupports.Remove(id);
            foreach (var kv in s.Drives)
            {
                _walkDrives.Remove(kv.Key); _driveLimbs.Remove(kv.Key);
                try { RestoreDrive(kv.Value); } catch (Exception e) { FruitBodies.ReportOnce("release walk drive", e); }
            }
            var f = s.Friction;
            _walkFeet.Remove(s.LeftFoot); _walkFeet.Remove(s.RightFoot);
            if (f != null && !f.WasCollected)
            {
                // Remove mappings first: the native call must restore the original material.
                try { f.SetDefaultFriction(); } catch (Exception e) { FruitBodies.ReportOnce("release walk material", e); }
            }
            if (s.Stance != null) UnityEngine.Object.Destroy(s.Stance);
            if (s.Swing != null) UnityEngine.Object.Destroy(s.Swing);
        }
        private static void ResetWalkSupport()
        {
            _supportDone.Clear(); _supportDone.AddRange(_walkSupports.Keys);
            foreach (int id in _supportDone) ReleaseSupport(id);
            _walkFeet.Clear(); _walkDrives.Clear(); _driveLimbs.Clear();
        }
        [HarmonyPatch(typeof(DefaultMuscleForce), nameof(DefaultMuscleForce.SyncJointDriveWithMuscleForceValue))]
        private static class WalkMuscleSync
        {
            static void Prefix(DefaultMuscleForce __instance) { try { if (_walkDrives.TryGetValue(__instance.Pointer, out var d)) RestoreDrive(d); } catch (Exception e) { FruitBodies.ReportOnce("walk drive restore", e); } }
            static void Postfix(DefaultMuscleForce __instance) { try { if (_walkDrives.TryGetValue(__instance.Pointer, out var d)) ApplyDrive(d); } catch (Exception e) { FruitBodies.ReportOnce("walk drive sync", e); } }
        }
        [HarmonyPatch(typeof(PelvisMuscleForce), nameof(PelvisMuscleForce.SyncJointDriveWithBalanceValue))]
        private static class WalkPelvisSync
        {
            static void Prefix(PelvisMuscleForce __instance) { try { if (_walkDrives.TryGetValue(__instance.Pointer, out var d)) RestoreDrive(d); } catch (Exception e) { FruitBodies.ReportOnce("walk pelvis restore", e); } }
            static void Postfix(PelvisMuscleForce __instance) { try { if (_walkDrives.TryGetValue(__instance.Pointer, out var d)) ApplyDrive(d); } catch (Exception e) { FruitBodies.ReportOnce("walk pelvis sync", e); } }
        }
        [HarmonyPatch(typeof(FootFrictionControl), nameof(FootFrictionControl.ApplyMaterial))]
        private static class WalkFootFriction
        {
            static void Prefix(Il2CppLVA.Limbs.LimbPhysics footPhysics, ref PhysicsMaterial material)
            {
                try
                {
                    if (footPhysics == null || material == null || !_walkFeet.TryGetValue(footPhysics.Pointer, out var s) || !s.Traction) return;
                    var f = s.Friction;
                    if (f.m_zeroFriction != null && material.Pointer == f.m_zeroFriction.Pointer && s.Swing != null) material = s.Swing;
                    else if (f.m_defaultFriction != null && material.Pointer == f.m_defaultFriction.Pointer && s.Stance != null) material = s.Stance;
                }
                catch (Exception e) { FruitBodies.ReportOnce("walk foot material", e); }
            }
        }
    }
}
