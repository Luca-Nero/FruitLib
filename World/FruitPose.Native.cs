using System;
using HarmonyLib;
using Il2CppLVA.Limbs.Systems;
using Il2CppLVA.Puppeteers.Variants.Humanoid;

namespace FruitLib
{
    // Where a held pose takes the stance over from the game. Every hook returns straight away
    // while nothing is held. All of these are direct calls in the Release build (Ghidra group
    // humanoid-stance): HumanoidMoveState.OnFixedUpdate calls UpdateBalance and then CanStand,
    // OnReprojectorDone calls SetGrounderWeight; DefaultMuscleForce's sync is called on its own.
    internal static class FruitPoseNative
    {
        /// <summary>Balance and GeneralMuscleForce from the puppeteer: support and full muscle
        /// for a held body, instead of the grounded ray and the pelvis tilt.</summary>
        [HarmonyPatch(typeof(MoveStateDynamicBalanceControl), nameof(MoveStateDynamicBalanceControl.UpdateBalance))]
        private static class Patch_UpdateBalance
        {
            static bool Prefix(MoveStateDynamicBalanceControl __instance)
            {
                // An exception escaping a Harmony prefix runs into the game's native code.
                try
                {
                    if (!FruitPose.AnyHeld) return true;
                    var rt = __instance.m_runtimeData;
                    return rt == null || !FruitPose.OwnBalance(rt.Pointer);
                }
                catch (Exception e) { FruitBodies.ReportOnce("pose balance hook", e); return true; }
            }
        }

        /// <summary>The move state's "can it still stand": a held body isn't judged on its tilt.</summary>
        [HarmonyPatch(typeof(HumanoidStandabilityChecker), nameof(HumanoidStandabilityChecker.CanStand))]
        private static class Patch_CanStand
        {
            static bool Prefix(HumanoidStandabilityChecker __instance, ref bool __result)
            {
                try
                {
                    if (!FruitPose.AnyHeld) return true;
                    var rt = __instance.m_runtimeData;
                    if (rt == null) return true;
                    bool? ours = FruitPose.OwnStandability(rt.Pointer, rt.CreaturePelvisVelocitySqr, rt.CreaturePelvisUpToGlobalUpDot);
                    if (ours == null) return true;
                    __result = ours.Value;
                    return false;
                }
                catch (Exception e) { FruitBodies.ReportOnce("pose standability hook", e); return true; }
            }
        }

        /// <summary>Leaving the move state (a fall, agony): the pose lets go of its bones first.</summary>
        [HarmonyPatch(typeof(HumanoidMoveState), nameof(HumanoidMoveState.Exit))]
        private static class Patch_MoveExit
        {
            static void Prefix(HumanoidMoveState __instance)
            {
                try
                {
                    if (!FruitPose.AnyHeld) return;
                    var rt = __instance.m_runtimeData;
                    if (rt != null) FruitPose.OnMoveStateExit(rt.Pointer);
                }
                catch (Exception e) { FruitBodies.ReportOnce("pose move-exit hook", e); }
            }
        }

        /// <summary>The rig's foot placement: held off while the legs are posed (it would pull the
        /// bent legs straight to the ground under a rig that stands at full height).</summary>
        [HarmonyPatch(typeof(GrounderIKTools), nameof(GrounderIKTools.SetGrounderWeight))]
        private static class Patch_GrounderWeight
        {
            static void Prefix(GrounderIKTools __instance, ref float valueNormalized)
            {
                try
                {
                    if (FruitPose.AnyGrounderHeld) valueNormalized = FruitPose.GrounderWeight(__instance.Pointer, valueNormalized);
                }
                catch (Exception e) { FruitBodies.ReportOnce("pose grounder hook", e); }
            }
        }

        /// <summary>Each limb's joint drive: the game's muscle value times the pose's tension.
        /// Never hook JointMuscleSlerpDriveSetter.Set: its Nullable&lt;float&gt; argument is passed in a
        /// register and Il2CppInterop's trampoline dereferences it (NRE at null, which skips the
        /// game's call; an access violation for a real damper value - 5.31.0/5.31.1).</summary>
        [HarmonyPatch(typeof(DefaultMuscleForce), nameof(DefaultMuscleForce.SyncJointDriveWithMuscleForceValue))]
        private static class Patch_MuscleSync
        {
            static void Postfix(DefaultMuscleForce __instance)
            {
                try
                {
                    if (FruitPose.AnyMuscleScaled) FruitPose.AfterMuscleSync(__instance.Pointer);
                }
                catch (Exception e) { FruitBodies.ReportOnce("pose muscle hook", e); }
            }
        }
    }
}
