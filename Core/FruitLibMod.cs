﻿using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppPresenters.Pause;
using Il2CppViews.Pause;
using MelonLoader;

[assembly: MelonInfo(typeof(FruitLib.FruitLibMod), FruitLib.FruitVersion.MelonName,
                     FruitLib.FruitVersion.VersionConst, FruitLib.FruitVersion.MelonAuthor)]
[assembly: MelonGame]

namespace FruitLib
{
    // ── MelonMod entry point ──────────────────────────────────────────────────
    public class FruitLibMod : MelonMod
    {
        private PauseView _pauseVC;
        private int _pollCountdown;
        private bool _wasMenuOpen;

        public override void OnInitializeMelon()
        {
            // No HarmonyInstance.PatchAll() here. MelonLoader already patches every mod assembly
            // unless it is marked [HarmonyDontPatchAll], so calling it again installed every hook
            // twice: each prefix and postfix ran twice, and each native method was detoured a
            // second time. That went unnoticed while the hooks were the toolbar's, which rarely
            // fired. The release build's equip path runs through six of them on every equip, and
            // intermittent memory-protection crashes started exactly there.

            FruitHud.Init();
            FruitWorldMenu.MapReset += FruitBallistics.OnMapReset;
            FruitWorldMenu.MapReset += OnWorldReset;
            FruitWorldEvents.Init();
            FruitEventLog.Init();
            FruitPerfMon.RegisterPanel();
            FruitUpdateCheck.RegisterPanel();
            FruitUpdateCheck.Register("FruitLib", FruitVersion.Current, "Luca-Nero", "FruitLib");

            LoggerInstance.Msg($"FruitLib v{FruitVersion.Current} ready.");
        }

        /// <summary>
        /// Per-subsystem so that one of them throwing does not take the rest down with it.
        ///
        /// These all hang off a single OnUpdate, so an exception out of the first one used to
        /// mean the pause menu was never polled, the MODS button was never built and the
        /// settings were unreachable - a toolbar bug reading as a menu bug. The names are the
        /// subsystems rather than a blanket try, so the log says which one is failing.
        /// </summary>
        private static readonly HashSet<string> _reportedTickFailures = new HashSet<string>();

        private static void Safely(string subsystem, Action tick)
        {
            try { tick(); }
            catch (Exception e)
            {
                // Once each. This is a per-frame path, so a repeated failure would otherwise
                // fill the log faster than anything else could be read in it.
                if (_reportedTickFailures.Add(subsystem))
                    MelonLogger.Warning($"[FruitLib] {subsystem} threw during update; the other " +
                                        $"subsystems carry on and this is reported once per scene: {e}");
            }
        }

        public override void OnUpdate()
        {
            Safely("FruitPerfMon", FruitPerfMon.Tick);
            Safely("FruitHud",     FruitHud.Tick);
            Safely("FruitInventory", FruitInventory.Tick);
            Safely("FruitMenu",    PauseTick);
            Safely("FruitBallistics",      FruitBallistics.Tick);
            Safely("FruitBallisticsProbe", FruitBallisticsProbe.Tick);
            Safely("FruitBodies",          FruitBodies.Tick);
            Safely("FruitItems",           FruitItems.Tick);
            Safely("FruitWorldEvents",     FruitWorldEvents.Tick);
            Safely("FruitEvents",          FruitEvents.Tick);
            Safely("FruitEventLog",        FruitEventLog.Tick);
            Safely("FruitSightProbe",      FruitSightProbe.Tick);
            Safely("FruitNav",             FruitNav.Tick);
            Safely("FruitNavDraw",         FruitNavDraw.Tick);
            Safely("FruitNavProbe",        FruitNavProbe.Tick);
            Safely("FruitHandsProbe",      FruitHandsProbe.Tick);
            Safely("FruitGaitProbe",       FruitGaitProbe.Tick);
#if FRUITLIB_DEVTOOLS
            Safely("FruitBundleProbe",     FruitBundleProbe.Tick);
#endif
        }

        private void PauseTick()
        {
            if (_pauseVC == null)
            {
                if (--_pollCountdown > 0) return;
                _pollCountdown = 60;
                _pauseVC = FruitScene.First<PauseView>();
                FruitMenu.PauseVC = _pauseVC;
                return;
            }

            FruitMenuNative.Tick(_pauseVC);
            FruitMenuScreen.Tick(_pauseVC);
#if FRUITLIB_DEVTOOLS
            FruitMenuProbe.Tick(_pauseVC);
#endif
            FruitMenu.OnPauseStateChanged(_pauseVC.IsOpen);

            bool isNowOpen = FruitMenu.IsOpen;
            FruitMenu.JustClosed = _wasMenuOpen && !isNowOpen;
            _wasMenuOpen = isNowOpen;

            // Settings changed for MelonPreferences mods take effect as the game resumes.
            if (FruitMenu.JustClosed) FruitPreferencesBridge.Flush();
        }

        public override void OnApplicationQuit()
        {
            Safely("FruitMenu", FruitPreferencesBridge.Flush);
            Safely("FruitEventLog", FruitEventLog.Shutdown);
        }

        // The terminal's RESET MAP / ALL deletes the bodies in place, with no scene load.
        // The tracker's scan would drop them a second later anyway; this just stops
        // half-gone bodies from raising events meanwhile.
        private static void OnWorldReset()
        {
            Safely("FruitBodyControl", FruitBodyControl.ResetForScene);
            Safely("FruitBodies", FruitBodies.ResetForScene);
            Safely("FruitSight", FruitSight.ResetForScene);
            Safely("FruitItems", FruitItems.ResetForScene);
            Safely("FruitPropMenu", FruitPropMenu.ResetForScene);
            Safely("FruitEvents", FruitEvents.ResetForScene);
            Safely("FruitWorldEvents", FruitWorldEvents.ResetForScene);
        }

        public override void OnGUI() { FruitMenu.Draw(); FruitHud.Draw(); FruitTextInput.OnGui(); }

        // After every Update (the game's DynamicYTargetRotation among them), before the next FixedUpdate.
        public override void OnLateUpdate()
        {
            Safely("FruitBodyControl", FruitBodyControl.LateTick);
            Safely("FruitHandsProbe late", FruitHandsProbe.LateTick);
        }

        // Physics rate: the step assist pushes limbs; held poses blend their bones.
        public override void OnFixedUpdate()
        {
            Safely("FruitBodyControl steps", FruitBodyControl.FixedTick);
            Safely("FruitPose", FruitPose.FixedTick);
            Safely("FruitGaitProbe", FruitGaitProbe.FixedTick);
        }

        // Mod maps build here: after the host scene's Awake, before its Start, which is when
        // the player and the first human are placed.
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
            => Safely("FruitMaps", () => FruitMaps.OnSceneLoaded(sceneName));

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
            => Safely("FruitMaps", () => FruitMaps.OnSceneUnloaded(sceneName));

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Safely("FruitMenu", FruitPreferencesBridge.Flush);
            _pauseVC      = null;
            _pollCountdown = 0;
            FruitMenu.PauseVC = null;
            _reportedTickFailures.Clear();
#if FRUITLIB_DEVTOOLS
            FruitMenuProbe.ResetForScene();
#endif
            FruitBallistics.ResetForScene();
            FruitBallisticsProbe.ResetForScene();
            FruitSightProbe.ResetForScene();
            FruitNavProbe.ResetForScene();
            FruitHandsProbe.ResetForScene();
            FruitGaitProbe.ResetForScene();
            FruitNavDraw.Clear();
            FruitNav.ResetForScene();
#if FRUITLIB_DEVTOOLS
            FruitBundleTests.ResetForScene();
#endif
            FruitMenuScreen.ResetForScene();
            FruitMenuNative.ResetForScene();
            FruitWorldMenu.ResetForScene();
            FruitGameMenus.ResetForScene();
            FruitTextInput.ResetForScene();
            FruitInventory.ResetForScene();
            FruitBodyControl.ResetForScene();
            FruitBodies.ResetForScene();
            FruitSight.ResetForScene();
            FruitEvents.ResetForScene();
            FruitWorldEvents.ResetForScene();
        }

        // PauseView raises nothing itself - it is a dumb view that owns a ManagedEvent
        // per button, and PausePresenter is what turns the Continue click into an
        // unpause. RequestUnpause is that handler: Continue only, not Esc.
        [HarmonyPatch(typeof(PausePresenter), nameof(PausePresenter.RequestUnpause))]
        static class Patch_PauseResume
        {
            // An exception escaping a Harmony prefix runs into the game's native code.
            static void Prefix()
            {
                try { FruitMenu.OnGameResumed(); }
                catch (Exception e) { MelonLogger.Warning($"[FruitLib] resume hook failed: {e.Message}"); }
            }
        }
    }
}
