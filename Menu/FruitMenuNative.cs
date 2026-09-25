using System;
using HarmonyLib;
using Il2CppInfrastructure.Components.ManagedBehaviours;
using Il2CppInfrastructure.Project.AssetsHandlers.SFX;
using Il2CppPresenters.Pause;
using Il2CppViews.Generic;
using Il2CppViews.Pause;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;

namespace FruitLib
{
    /// <summary>
    /// FruitLib's entry in the pause menu, built out of the game's own button.
    ///
    /// The alternative - and what this replaces - is an IMGUI box floating in the corner of
    /// the pause screen, which reads as something bolted on because it is. A MenuLineButton
    /// cloned from SETTINGS inherits the plate, the console font, the hover flash and the
    /// selection colours for free, and sits in the column where a player already looks.
    ///
    /// Nothing here touches the screen state machine. The button is a child of the pause
    /// root like the buttons it sits between, so it opens, closes and hides exactly when
    /// they do, and <see cref="FruitMenu"/>'s existing hide/restore pass covers it.
    /// </summary>
    internal static class FruitMenuNative
    {
        /// <summary>The word on the plate. Upper case to match CONTINUE / SETTINGS / QUIT.</summary>
        internal static string Label = "MODS";

        private static MenuLineButton _button;
        private static UnityAction    _onClick;   // held so il2cpp's copy is not collected
        private static bool           _failed;
        private static bool           _tickFlashOurselves;

        /// <summary>Whether the pause menu is carrying our button. False means FruitMenu draws its own.</summary>
        internal static bool Present => _button != null;

        /// <summary>
        /// Frames spent waiting for a settings button to clone before giving up.
        ///
        /// m_settingsButton is a serialized reference and should be live the moment the view
        /// is, so this is only ever cover for being handed a PauseView mid-construction. It
        /// is a budget rather than a single shot because one unlucky frame is not a reason
        /// to spend the whole scene on the fallback button.
        /// </summary>
        private const int WaitFrames = 120;

        private static int _waited;

        internal static void ResetForScene()
        {
            _button             = null;
            _onClick            = null;
            _failed             = false;
            _waited             = 0;
            _tickFlashOurselves = false;
        }

        internal static void Tick(PauseView view)
        {
            if (_button != null)
            {
                // Unscaled: the pause menu runs at timeScale 0, so deltaTime is zero here and
                // a flash driven by it would never move.
                if (_tickFlashOurselves)
                {
                    try { _button.TickFlash(Time.unscaledDeltaTime); }
                    catch (Exception e)
                    {
                        _tickFlashOurselves = false;
                        MelonLogger.Warning($"[FruitMenu] flash tick failed, leaving the button static: {e.Message}");
                    }
                }
                return;
            }

            if (_failed || view == null) return;
            Build(view);
        }

        private static void Build(PauseView view)
        {
            try
            {
                var proto  = view.m_settingsButton;
                var parent = proto != null ? proto.transform.parent : null;
                if (proto == null || parent == null)
                {
                    if (++_waited < WaitFrames) return;
                    GiveUp(proto == null
                        ? "PauseView has no settings button to clone"
                        : "the settings button has no parent to clone into");
                    return;
                }

                var clone = FruitMenuClone.Make(proto, parent, "FruitLib_ModsButton");
                if (clone == null) { GiveUp("the copy came back null"); return; }

                // If nothing was there to inject, the clone will never join the update loop
                // and its flash has to be driven from here instead.
                bool injected = clone.m_coreServicesProvider != null;
                if (!injected)
                {
                    _tickFlashOurselves = true;
                    MelonLogger.Msg("[FruitMenu] no core services provider to copy; " +
                                    "driving the MODS button's flash from FruitLib instead.");
                }

                // Straight after SETTINGS, so the column reads CONTINUE / SETTINGS / MODS / QUIT.
                clone.transform.SetSiblingIndex(proto.transform.GetSiblingIndex() + 1);

                clone.gameObject.SetActive(true);   // Awake runs here, provider in hand

                // After waking, not before: the word is drawn by a ConsoleTextEffect that has
                // to have run its own Awake before it has anywhere to type into.
                clone.SetWord(Label);

                var button = clone.m_button;
                if (button == null)
                {
                    UnityEngine.Object.Destroy(clone.gameObject);
                    GiveUp("the cloned button has no UI Button to listen on");
                    return;
                }

                // Listening on the Unity button rather than the clone's ManagedEvent: the event
                // needs a SingleShotActionsBag to release the subscription into, and the only
                // bags to hand belong to the view and are fired on its own schedule. onClick is
                // ours alone, and the clone's native listener still drives its click flash.
                _onClick = (UnityAction)(Action)OnClicked;
                button.onClick.AddListener(_onClick);

                _button = clone;
                MelonLogger.Msg($"[FruitMenu] native '{Label}' button inserted after SETTINGS " +
                                $"(provider={(injected ? "copied" : "absent")}).");
            }
            catch (Exception e) { GiveUp(e.Message); }
        }

        private static void OnClicked()
        {
            try
            {
                // The native screen when there is one, the IMGUI panel when there is not.
                // Falling through rather than doing nothing matters: the screen is a copy of
                // a game object that a future build may move or rename, and mod settings
                // being reachable is worth more than them being pretty.
                FruitSfx.PlayUI(UISFXType.LargeButtonClick);

                if (FruitMenuScreen.Present) FruitMenuScreen.Show();
                else                         FruitMenu.OpenFromNativeButton();
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] opening mod settings failed: {e.Message}"); }
        }

        /// <summary>
        /// Stop trying for this scene and say why once.
        ///
        /// Retrying every frame would turn one bad assumption about the pause menu into a log
        /// flood, and there is nothing to retry: the view either has a button to clone or it
        /// does not. FruitMenu falls back to its own button, so the settings stay reachable
        /// either way - this is a cosmetic failure, not a functional one.
        /// </summary>
        private static void GiveUp(string reason)
        {
            _failed = true;
            MelonLogger.Warning($"[FruitMenu] could not build the native '{Label}' button ({reason}); " +
                                "falling back to the corner button.");
        }
    }

    /// <summary>
    /// Makes ESC step back out of the mod settings instead of unpausing.
    ///
    /// ESC is read by PauseToggleButton, which asks its IPauseBackNavigation to step back and
    /// unpauses outright when the answer is no. The presenter answers for the game's own
    /// screens, and FruitLib's panel is not one of them, so on the pause root the honest
    /// answer is no - and the game closes the whole menu out from under an open panel.
    /// Answering for FruitLib first is the whole fix: one screen deep is one screen deep,
    /// whoever drew it.
    /// </summary>
    [HarmonyPatch(typeof(PausePresenter), nameof(PausePresenter.Services_UI_IPauseBackNavigation_TryStepBack))]
    internal static class FruitMenu_BackNavigationPatch
    {
        /// <summary>
        /// The frame we last answered on, so one key press cannot count twice.
        ///
        /// Something in this menu asks twice per press - the probe shows DrawScreen logging
        /// the same move twice on the same frame, and TryStepBack arrives the same way. The
        /// game copes because a second step back through its own screens lands on the screen
        /// it is already showing; ours popped a page each time, so ESC went down two levels
        /// and then out of the pause menu entirely.
        /// </summary>
        private static int _answeredOn = -1;

        static bool Prefix(ref bool __result)
        {
            // Already answered this press. Swallowed rather than passed on, because by now
            // our stack may be empty and the game would read that as "nothing to step back
            // to" and close the menu.
            if (_answeredOn == Time.frameCount) { __result = true; return false; }

            // A row waiting for a key is the innermost thing of all, and cancelling is what
            // ESC means there. It also means ESC can itself be bound to something, since the
            // waiting row consumes the press before any of this.
            if (FruitMenuScreen.Listening)
            {
                _answeredOn = Time.frameCount;
                FruitMenuScreen.CancelListening();
                __result = true;
                return false;
            }

            // Then innermost first, so ESC unwinds panel -> pages -> pause root one step at
            // a time, the way it would through the game's own screens.
            if (FruitMenu.IsPanelOpen)
            {
                _answeredOn = Time.frameCount;
                FruitMenu.StepBackFromPanel();
                __result = true;
                return false;
            }

            if (FruitMenuScreen.IsOpen)
            {
                _answeredOn = Time.frameCount;
                FruitMenuScreen.Back();
                __result = true;
                return false;
            }

            return true;   // nothing of ours is up; let the game answer
        }
    }
}
