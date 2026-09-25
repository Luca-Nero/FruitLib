using System;
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
            Safely("FruitBundleProbe",     FruitBundleProbe.Tick);
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
            FruitMenuProbe.Tick(_pauseVC);
            FruitMenu.OnPauseStateChanged(_pauseVC.IsOpen);

            bool isNowOpen = FruitMenu.IsOpen;
            FruitMenu.JustClosed = _wasMenuOpen && !isNowOpen;
            _wasMenuOpen = isNowOpen;
        }

        public override void OnGUI() { FruitMenu.Draw(); FruitHud.Draw(); }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            _pauseVC      = null;
            _pollCountdown = 0;
            FruitMenu.PauseVC = null;
            _reportedTickFailures.Clear();
            FruitMenuProbe.ResetForScene();
            FruitBallistics.ResetForScene();
            FruitBallisticsProbe.ResetForScene();
            FruitBundleTests.ResetForScene();
            FruitMenuScreen.ResetForScene();
            FruitMenuNative.ResetForScene();
            FruitInventory.ResetForScene();
        }

        // The pause menu was rebuilt for the Steam demo build. PauseView no longer
        // raises anything itself - it is a dumb view that owns a ManagedEvent per
        // button, and PausePresenter is what turns the Continue click into an
        // unpause. RequestUnpause is that handler, so it is the 1:1 successor to
        // 0.14's PauseView.RaiseContinueRequested: Continue only, not Esc.
        [HarmonyPatch(typeof(PausePresenter), nameof(PausePresenter.RequestUnpause))]
        static class Patch_PauseResume
        {
            static void Prefix() => FruitMenu.OnGameResumed();
        }
    }
}
