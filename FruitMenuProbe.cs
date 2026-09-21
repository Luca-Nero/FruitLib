using System;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppPresenters.Pause;
using Il2CppViews.ContextMenu;
using Il2CppViews.Generic;
using Il2CppViews.Inspection;
using Il2CppViews.Pause;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// Reads the pause menu's screen machinery out loud, so a port can be designed against
    /// what it does rather than what the decompiled stubs imply.
    ///
    /// The menu never changes scene. One canvas holds every screen as a sibling object, a
    /// MenuScreenStack&lt;PauseScreenId&gt; says which id should be up, and each screen owns a
    /// ScreenTransition that rides a 0..1 value. The wipe is a RevealEdgeScreenTransition:
    /// a RectMask2D whose rect is driven by that value, with a drawn edge sorted above the
    /// window. Interop stubs carry no method bodies, so the ordering between those parts -
    /// who closes before who opens, what the chrome does in between - can only be read off a
    /// running game.
    ///
    /// Off by default. It is a development tool, not a feature.
    /// </summary>
    internal static class FruitMenuProbe
    {
        private static bool _dumpedThisScene;
        private static bool _tracing;
        private static int  _traceFrames;

        /// <summary>Frames of trace to keep printing after the last moving part stops.</summary>
        private const int TraceTail = 5;

        internal static bool Enabled => FruitHudConfig.MenuProbe;

        internal static void ResetForScene()
        {
            _dumpedThisScene = false;
            _tracing         = false;
            _traceFrames     = 0;
        }

        internal static void Tick(PauseView view)
        {
            if (!Enabled || view == null) return;

            if (FruitHudConfig.MenuProbeKey != KeyCode.None && Input.GetKeyDown(FruitHudConfig.MenuProbeKey))
                DumpStructure(view, "on request");

            if (!_dumpedThisScene && view.IsOpen)
            {
                _dumpedThisScene = true;
                DumpStructure(view, "pause menu first opened");
            }

            Trace(view);
        }

        // ── Structure ────────────────────────────────────────────────────────────

        private static void DumpStructure(PauseView view, string why)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[FruitMenuProbe] ===== pause menu structure ({why}) =====");
                sb.AppendLine($"  PauseView      {Path(view.transform)}");
                sb.AppendLine($"    IsOpen={Safe(() => view.IsOpen.ToString())} " +
                              $"stepping={Safe(() => view.IsSteppingBack.ToString())} " +
                              $"path=[{StackPath()}]");

                // m_root and m_current are deliberately not read. They are TScreenId fields on
                // a generic class, and Il2CppInterop resolves their offset once off the open
                // generic - so every instantiation reads the same wrong address and every id
                // comes back as the same large number. The presenter's stack holds the same
                // information as a List<PauseScreenId>, which reads correctly, and the screens
                // below are named by their GameObject, which cannot be wrong.

                sb.AppendLine($"    chrome         {Describe(view.m_chrome)}");
                sb.AppendLine($"    backdrop       {(view.m_backdrop == null ? "none" : Path(view.m_backdrop.transform))}");
                sb.AppendLine($"    navigation     {(view.m_navigation == null ? "none" : Path(view.m_navigation.transform))}" +
                              $" root='{Safe(() => view.m_navigation.m_rootAction)}'" +
                              $" deeper='{Safe(() => view.m_navigation.m_deeperAction)}'");

                var screens = view.m_screens;
                sb.AppendLine($"    screens[{(screens == null ? 0 : screens.Length)}]:");
                if (screens != null)
                {
                    for (int i = 0; i < screens.Length; i++)
                    {
                        var s = screens[i];
                        if (s == null) { sb.AppendLine($"      [{i}] null"); continue; }

                        sb.AppendLine($"      [{i}] {Label(s),-10} {Path(s.transform)}");
                        sb.AppendLine($"           type={TypeName(s)} " +
                                      $"shown={Safe(() => s.IsShown.ToString())} " +
                                      $"hidden={Safe(() => s.IsHidden.ToString())} " +
                                      $"restingShown={Safe(() => s.RestingShown.ToString())} " +
                                      $"standsApart={Safe(() => s.StandsApart.ToString())}");
                        sb.AppendLine($"           ride={TypeName(s.Ride)} timings={Safe(() => s.Timings.ToString())}");
                    }
                }

                MelonLogger.Msg(sb.ToString());
                DumpTransitions(view);
                DumpWidgets(view);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenuProbe] structure dump failed: {e}"); }
        }

        /// <summary>
        /// Every ScreenTransition under the pause menu, whether or not a screen claims it.
        ///
        /// AbstractScreenView.Ride is null until the screen has woken, and a screen that has
        /// never been shown may not have woken yet - so the components are worth listing on
        /// their own to see which flavour of transition each screen was authored with.
        /// </summary>
        private static void DumpTransitions(PauseView view)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[FruitMenuProbe] ----- transitions under the pause menu -----");
                foreach (var t in view.GetComponentsInChildren<ScreenTransition>(true))
                {
                    if (t == null) continue;
                    sb.AppendLine($"  {Describe(t)}");
                }
                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenuProbe] transition dump failed: {e}"); }
        }

        /// <summary>
        /// The settings controls the game already owns, and where to find one of each.
        ///
        /// These are the prototypes a native mod settings screen would be built from: a
        /// SettingSlider per number, a SettingToggle per flag, a SettingChoice per set of
        /// named options. Each entry is a live instance under the pause menu, so the path is
        /// what to clone from.
        /// </summary>
        private static void DumpWidgets(PauseView view)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[FruitMenuProbe] ----- settings controls in the pause menu -----");

                Inventory<SettingSlider>(view, sb, "SettingSlider");
                Inventory<NamedSlider>(view, sb, "NamedSlider");
                Inventory<SettingToggle>(view, sb, "SettingToggle");
                Inventory<SettingChoice>(view, sb, "SettingChoice");
                Inventory<SettingTable>(view, sb, "SettingTable");
                Inventory<MenuLineButton>(view, sb, "MenuLineButton");

                sb.AppendLine($"  IMonoFactory: {FindMonoFactory()}");
                sb.AppendLine($"  SettingOptionGroup.m_monoFactory: {ProbeGroupFactory(view)}");
                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenuProbe] widget dump failed: {e}"); }
        }

        private static void Inventory<T>(PauseView view, StringBuilder sb, string label)
            where T : Component
        {
            try
            {
                var found = view.GetComponentsInChildren<T>(true);
                int n = found == null ? 0 : found.Length;
                sb.AppendLine($"  {label} x{n}");
                if (found == null) return;

                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] == null) continue;
                    sb.AppendLine($"      {Path(found[i].transform)}");
                }
            }
            catch (Exception e) { sb.AppendLine($"  {label}: read failed ({e.Message})"); }
        }

        /// <summary>
        /// Whether m_monoFactory can be read off a live SettingChoice.
        ///
        /// It is declared on SettingOptionGroup&lt;,&gt;, a generic class, and this menu has
        /// already shown what Il2CppInterop does to some of those members - every screen
        /// reporting the same id. The broken ones were all typed by the generic parameter
        /// itself; m_monoFactory is a plain IMonoFactory, so it should be fine. "Should" is
        /// not good enough to design a keybind table on, hence this.
        /// </summary>
        private static string ProbeGroupFactory(PauseView view)
        {
            try
            {
                var choices = view.GetComponentsInChildren<SettingChoice>(true);
                if (choices == null || choices.Length == 0) return "no SettingChoice in the menu to read";

                foreach (var choice in choices)
                {
                    if (choice == null) continue;

                    var factory = choice.m_monoFactory;
                    if (factory == null) return $"read as null on '{choice.gameObject.name}'";

                    var ctx = FruitScene.First<ContextMenuWindow>();
                    bool same = ctx != null && ctx.m_monoFactory != null &&
                                ctx.m_monoFactory.Pointer == factory.Pointer;

                    return $"readable on '{choice.gameObject.name}' " +
                           $"(0x{factory.Pointer.ToInt64():X}, {(same ? "same object as ContextMenuWindow's" : "a different instance")})";
                }

                return "every SettingChoice was null";
            }
            catch (Exception e) { return $"threw ({e.Message})"; }
        }

        /// <summary>
        /// Where an IMonoFactory can be reached from, if anywhere.
        ///
        /// It is the game's own injection-aware Instantiate, which is the difference between
        /// a cloned control that works and one that sits inert - so it is worth knowing
        /// whether one is in reach before designing around cloning by hand. SettingOptionGroup
        /// holds one, but that field is on a generic class and Il2CppInterop reads generic
        /// fields off the open type at the wrong offset, so the holders checked here are all
        /// plain MonoBehaviours whose fields can be trusted.
        /// </summary>
        private static string FindMonoFactory()
        {
            try
            {
                var ctx = FruitScene.First<ContextMenuWindow>();
                if (ctx != null && ctx.m_monoFactory != null) return $"ContextMenuWindow at {Path(ctx.transform)}";

                var vitals = FruitScene.First<VitalsWindowView>();
                if (vitals != null && vitals.m_monoFactory != null) return $"VitalsWindowView at {Path(vitals.transform)}";

                var part = FruitScene.First<PartPopupView>();
                if (part != null && part.m_monoFactory != null) return $"PartPopupView at {Path(part.transform)}";

                return "not reachable from ContextMenuWindow / VitalsWindowView / PartPopupView";
            }
            catch (Exception e) { return $"lookup failed ({e.Message})"; }
        }

        private static string Describe(ScreenTransition t)
        {
            if (t == null) return "none";
            return $"{TypeName(t),-34} {Path(t.transform)} " +
                   $"timings={Safe(() => t.Timings.ToString())} " +
                   $"authoredOpen={Safe(() => t.AuthoredOpen.ToString())} " +
                   $"speedup={Safe(() => t.RideSpeedup.ToString("0.##"))}";
        }

        // ── Trace ────────────────────────────────────────────────────────────────

        /// <summary>
        /// One line a frame for as long as anything is moving, plus a short tail.
        ///
        /// The tail matters: the interesting part of a menu transition is the handover, and
        /// the frame where the last part settles is the one that says whether it settled
        /// shown or hidden.
        /// </summary>
        private static void Trace(PauseView view)
        {
            try
            {
                var sb = new StringBuilder();
                bool moving = false;

                var chrome = view.m_chrome;
                if (chrome != null)
                {
                    moving |= Safe(() => chrome.IsMoving, false);
                    sb.Append($"chrome[{Ride(chrome)}] ");
                }

                var screens = view.m_screens;
                if (screens != null)
                {
                    for (int i = 0; i < screens.Length; i++)
                    {
                        var s = screens[i];
                        if (s == null) continue;

                        bool inTransition = Safe(() => s.InTransition, false);
                        moving |= inTransition;

                        var ride = s.Ride;
                        if (ride == null) continue;

                        sb.Append($"{Label(s)}[" +
                                  $"{Safe(() => ride.Drawn.ToString("0.00"))} " +
                                  $"aims={Safe(() => ride.AimsShown ? "S" : "H")} " +
                                  $"ends={Safe(() => ride.EndsShown ? "S" : "H")}" +
                                  $"{(inTransition ? " *" : "")}] ");
                    }
                }

                if (moving) { _tracing = true; _traceFrames = TraceTail; }
                else if (_tracing && --_traceFrames <= 0) { _tracing = false; MelonLogger.Msg("[FruitMenuProbe] --- settled ---"); return; }

                if (_tracing) MelonLogger.Msg($"[FruitMenuProbe] f{Time.frameCount} {sb}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitMenuProbe] trace failed, stopping: {e.Message}");
                FruitHudConfig.MenuProbe = false;
            }
        }

        private static string Ride(ScreenTransition t) =>
            $"{Safe(() => t.Drawn.ToString("0.00"))} " +
            $"aims={Safe(() => t.AimsShown ? "S" : "H")} " +
            $"ends={Safe(() => t.EndsShown ? "S" : "H")}" +
            $"{(Safe(() => t.IsMoving, false) ? " *" : "")}";

        // ── The stack's own account of what happened ─────────────────────────────

        internal static void OnDrawScreen(PausePresenter presenter)
        {
            if (!Enabled) return;
            try
            {
                var stack = presenter.m_stack;
                if (stack == null) { MelonLogger.Msg("[FruitMenuProbe] DrawScreen: presenter has no stack"); return; }

                // m_stack.m_path rather than the Path property: the interop wrapper for
                // IReadOnlyList<T> carries only an indexer, so there is no Count to walk to.
                var path = stack.m_stack != null ? stack.m_stack.m_path : null;
                var sb = new StringBuilder("[FruitMenuProbe] DrawScreen -> path [");
                if (path != null)
                    for (int i = 0; i < path.Count; i++) sb.Append(i > 0 ? " / " : "").Append(path[i].ToString());
                sb.Append($"] at f{Time.frameCount}");

                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenuProbe] DrawScreen log failed: {e.Message}"); }
        }

        // ── Small helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// A screen's name, taken from its GameObject rather than its id.
        ///
        /// ScreenView&lt;TScreenId&gt;.m_id and .Id both resolve their il2cpp handle off the
        /// open generic type, so every instantiation reads the same wrong offset and every
        /// screen reports the same large number. The objects are named RootScreen,
        /// SettingsScreen and so on, which says the same thing and cannot be misread.
        /// </summary>
        private static string Label(Il2CppViews.Generic.ScreenView<PauseScreenId> s)
        {
            try
            {
                string n = s.name;
                return n != null && n.EndsWith("Screen") && n.Length > 6
                    ? n.Substring(0, n.Length - 6)
                    : n;
            }
            catch { return "?"; }
        }

        /// <summary>The pause stack's path, read off the presenter - see <see cref="Label"/>.</summary>
        private static string StackPath()
        {
            try
            {
                var presenter = FruitScene.First<PausePresenter>();
                var path = presenter?.m_stack?.m_stack?.m_path;
                if (path == null) return "?";

                var sb = new StringBuilder();
                for (int i = 0; i < path.Count; i++) sb.Append(i > 0 ? " / " : "").Append(path[i].ToString());
                return sb.ToString();
            }
            catch { return "?"; }
        }

        private static string Path(Transform t)
        {
            if (t == null) return "<no transform>";
            try
            {
                var sb = new StringBuilder(t.name);
                for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
                return sb.ToString();
            }
            catch { return "<path failed>"; }
        }

        private static string TypeName(Il2CppSystem.Object o)
        {
            try { return o == null ? "null" : o.GetIl2CppType().FullName; }
            catch { return "?"; }
        }

        private static string TypeName(Il2CppObjectBase o)
        {
            try { return o == null ? "null" : TypeName(o.TryCast<Il2CppSystem.Object>()); }
            catch { return "?"; }
        }

        private static string Safe(Func<string> read)
        {
            try { return read() ?? "null"; }
            catch { return "?"; }
        }

        private static T Safe<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch { return fallback; }
        }
    }

    /// <summary>Reports every stack move the pause menu makes, when the probe is on.</summary>
    [HarmonyPatch(typeof(PausePresenter), nameof(PausePresenter.DrawScreen))]
    internal static class FruitMenuProbe_DrawScreenPatch
    {
        static void Postfix(PausePresenter __instance) => FruitMenuProbe.OnDrawScreen(__instance);
    }
}
