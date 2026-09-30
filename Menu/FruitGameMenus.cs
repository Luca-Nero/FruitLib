using System;
using HarmonyLib;
using Il2CppUI.OpenMenus;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// Whether one of the game's own menus is open: the terminal (items / world), a context
    /// menu, and whatever else registers with the game's OpenMenus service. The game's own
    /// tools - cursor, placement, spawning - take that service in and stand down while any
    /// menu is up; <see cref="FruitMenu.IsInputSuppressed"/> folds it in so mods' items do too.
    ///
    /// The state is read off OpenMenus.Add / Remove, the only two ways it changes, rather than
    /// asked for each frame: it is a plain game object with no Unity lifetime to check.
    /// </summary>
    internal static class FruitGameMenus
    {
        private static bool _anyOpen;
        private static int  _closedFrame = -10;

        internal static bool AnyOpen => _anyOpen;

        /// <summary>The frame a menu closed on, and the one after: the click that closed it
        /// must not also reach whatever the player is holding.</summary>
        internal static bool JustClosed => Time.frameCount - _closedFrame <= 1;

        internal static void Track(OpenMenus menus)
        {
            bool open = menus.AnyOpen;
            if (_anyOpen && !open) _closedFrame = Time.frameCount;
            _anyOpen = open;
        }

        /// <summary>A new scene brings a new OpenMenus; a menu open across the load never
        /// reports closing to us.</summary>
        internal static void ResetForScene()
        {
            _anyOpen = false;
            _closedFrame = -10;
        }
    }

    [HarmonyPatch(typeof(OpenMenus), nameof(OpenMenus.Add))]
    internal static class FruitGameMenus_AddPatch
    {
        static void Postfix(OpenMenus __instance)
        {
            // An exception escaping a Harmony postfix runs into the game's native code.
            try { FruitGameMenus.Track(__instance); }
            catch (Exception e) { MelonLogger.Warning($"[FruitLib] menu-open hook failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(OpenMenus), nameof(OpenMenus.Remove))]
    internal static class FruitGameMenus_RemovePatch
    {
        static void Postfix(OpenMenus __instance)
        {
            try { FruitGameMenus.Track(__instance); }
            catch (Exception e) { MelonLogger.Warning($"[FruitLib] menu-close hook failed: {e.Message}"); }
        }
    }
}
