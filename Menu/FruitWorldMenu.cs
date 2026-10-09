using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppData.UI;
using Il2CppInfrastructure.Project.AssetsHandlers.SFX;
using Il2CppPresenters.Terminal;
using Il2CppUI.Terminal;
using Il2CppViews.ContextMenu;
using Il2CppViews.Terminal;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace FruitLib
{
    /// <summary>The six actions on the game's World &gt; SCENE page. Values match the game's own enum.</summary>
    public enum FruitSceneAction
    {
        DeleteNpc      = 0,
        DeleteBodies   = 1,
        ClearParticles = 2,
        ResetMap       = 3,
        ResetAll       = 4,
        ResetPlayer    = 5,
    }

    /// <summary>
    /// The World &gt; SCENE page of the in-game terminal: what happens when the player uses it,
    /// and room for a mod to put buttons of its own under the game's. (since 5.5.0)
    ///
    /// Two halves that share a file because they share a page.
    ///
    /// The events answer "the game just reset something". A mod that spawns things of its own
    /// - a placed charge, a decal, a sticky grenade - has no idea the player pressed RESET MAP,
    /// because the game's reset only knows about the game's own objects. <see cref="MapReset"/>
    /// is the cue to let go of them.
    ///
    /// The buttons are clones of one of the game's own rows, built the same way the MODS button
    /// on the pause menu is (see <see cref="FruitMenuNative"/>): plate, font, hover flash and
    /// click sound all come with the copy. What is never touched is the game's row list. The
    /// presenter checks it against the row enum in Awake and refuses to run if they disagree,
    /// so extra buttons live beside the array, not in it.
    /// </summary>
    public static class FruitWorldMenu
    {
        /// <summary>
        /// Raised after the game has run one SCENE row - once per press. RESET ALL is one press,
        /// so it raises <see cref="FruitSceneAction.ResetAll"/> once, not the steps inside it.
        /// </summary>
        public static event Action<FruitSceneAction> SceneActionRan;

        /// <summary>
        /// Raised after RESET MAP or RESET ALL, once per press: the map is back to how it was
        /// authored. Mods clear whatever they spawned here.
        ///
        /// The game destroys the map's spawned objects at the end of the frame rather than
        /// straight away, so do not test for them being gone from inside the handler. Clear
        /// your own bookkeeping and your own objects, not theirs.
        /// </summary>
        public static event Action MapReset;

        /// <summary>
        /// Adds a button under the game's rows, or replaces the one with the same id. The label
        /// is shown upper case, like the native ones.
        ///
        /// Safe to call at any time, including before the terminal exists: the registration is
        /// kept and the button is built whenever the page is. If the page is already up, the
        /// button appears (or its label changes) straight away.
        /// </summary>
        public static void AddButton(string id, string label, Action onClick)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("A world menu button needs an id.", nameof(id));

            var def = Find(id);
            if (def == null) _defs.Add(def = new Def { Id = id });
            def.Label   = label ?? "";
            def.OnClick = onClick;

            Refresh();
        }

        /// <summary>Removes a button added with <see cref="AddButton"/>. Unknown ids are ignored.</summary>
        public static void RemoveButton(string id)
        {
            if (id == null) return;

            var def = Find(id);
            if (def != null) _defs.Remove(def);

            Refresh();
        }

        // ── Registration ────────────────────────────────────────────────────────────────

        private sealed class Def
        {
            public string Id;
            public string Label;
            public Action OnClick;
        }

        /// <summary>One button on the live page, and what has been done to it.</summary>
        private sealed class Built
        {
            public string                Id;
            public ContextMenuActionButton Clone;
            public bool                  Drawn;
            public string                DrawnLabel;
        }

        // Registration order is display order, so a list rather than a dictionary.
        private static readonly List<Def>   _defs  = new List<Def>();
        private static readonly List<Built> _built = new List<Built>();
        // Every listener wired, held so il2cpp's copy is not collected, for as long as its button
        // lives: a button taken back by name has lost its Built entry, but not its listener. Keyed
        // by the button so the list lets go of them once the game destroys it (scene load, or
        // RemoveButton) instead of growing by one per button per scene.
        private sealed class Wired
        {
            public ContextMenuActionButton Clone;
            public UnityAction           Listener;
        }
        private static readonly List<Wired> _listeners = new List<Wired>();

        private static void PruneListeners()
        {
            for (int i = _listeners.Count - 1; i >= 0; i--)
                if (_listeners[i].Clone == null) _listeners.RemoveAt(i);
        }

        private static Def Find(string id)
        {
            foreach (var d in _defs) if (d.Id == id) return d;
            return null;
        }

        // ── The page we are attached to ─────────────────────────────────────────────────

        private static TerminalWorldPlateView _view;
        private static IntPtr                 _viewPtr;
        private static bool                   _pageUsable;

        private static ContextMenuActionButton _proto;
        private static Transform               _rowParent;
        private static bool                    _autoLayout;   // the parent has a LayoutGroup
        private static int                     _lastRowSibling;
        private static Vector2                 _bottomPos;
        private static Vector2                 _step;         // one row down

        private static bool _noPrototypeReported;

        /// <summary>The page last adopted, or null if there is none or it was destroyed.</summary>
        internal static TerminalWorldPlateView CurrentView => _view != null ? _view : null;

        /// <summary>Forget the page. Registrations and subscribers stay: they belong to the mods.</summary>
        internal static void ResetForScene()
        {
            _view       = null;
            _viewPtr    = IntPtr.Zero;
            _pageUsable = false;
            _proto      = null;
            _rowParent  = null;
            _built.Clear();
            _noPrototypeReported = false;
            PruneListeners();
        }

        /// <summary>Called from AddButton / RemoveButton: bring the live page, if there is one, up to date.</summary>
        private static void Refresh()
        {
            try
            {
                // The last page a hook saw. If it has been destroyed, Unity's null check says so
                // and the next hook will adopt the new one.
                if (_view != null) Sync(_view, false);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitWorldMenu] updating the SCENE page failed: {e.Message}"); }
        }

        /// <summary>
        /// Makes the page carry exactly the buttons that are registered.
        /// <paramref name="redraw"/> forces every label to be written again, for when the page has
        /// just been switched back on and may have wiped its text.
        /// </summary>
        internal static void Sync(TerminalWorldPlateView view, bool redraw)
        {
            if (view == null) return;

            if (_view == null || _viewPtr != view.Pointer) Adopt(view);
            if (!_pageUsable) return;

            // Buttons whose registration is gone, or whose object the game destroyed under us.
            for (int i = _built.Count - 1; i >= 0; i--)
            {
                var b = _built[i];
                if (b.Clone == null) { _built.RemoveAt(i); continue; }
                if (Find(b.Id) != null) continue;

                UnityEngine.Object.Destroy(b.Clone.gameObject);
                _built.RemoveAt(i);
            }

            foreach (var d in _defs)
                if (FindBuilt(d.Id) == null) Create(d);

            Layout();
            Draw(redraw);
        }

        private static Built FindBuilt(string id)
        {
            foreach (var b in _built) if (b.Id == id) return b;
            return null;
        }

        /// <summary>
        /// Takes a new page on: works out what to clone and where the copies go.
        ///
        /// Whether the rows sit in a layout group was not known when this was written, so both
        /// are handled. With a LayoutGroup on the parent the copies are simply placed after the
        /// last row in the hierarchy and the group stacks them. Without one the rows are
        /// hand-placed, so the copies are put below the lowest row, one row-spacing at a time,
        /// with the spacing read off the two lowest rows.
        /// </summary>
        private static void Adopt(TerminalWorldPlateView view)
        {
            _view       = view;
            _viewPtr    = view.Pointer;
            _pageUsable = false;
            _proto      = null;
            _rowParent  = null;
            _built.Clear();

            var rows = view.m_sceneRows;
            if (rows == null || rows.Length == 0) { NoPrototype("the plate has no scene rows"); return; }

            ContextMenuActionButton proto = null;
            foreach (var r in rows) if (r != null) { proto = r; break; }
            var parent = proto != null ? proto.transform.parent : null;
            if (proto == null || parent == null) { NoPrototype("the scene rows have no parent to clone into"); return; }

            _proto     = proto;
            _rowParent = parent;
            _autoLayout = parent.GetComponent<LayoutGroup>() != null;

            // Every row that shares the prototype's parent - the copies stack after these.
            int last = -1;
            var lows = new List<RectTransform>();
            foreach (var r in rows)
            {
                if (r == null || r.transform.parent != parent) continue;
                last = Math.Max(last, r.transform.GetSiblingIndex());
                var rt = r.GetComponent<RectTransform>();
                if (rt != null) lows.Add(rt);
            }
            _lastRowSibling = last;

            // The two lowest rows on screen. Array order is not necessarily top-to-bottom (the
            // presenter keeps its own top-down list), so sort by height instead.
            lows.Sort((a, b) => a.anchoredPosition.y.CompareTo(b.anchoredPosition.y));
            if (lows.Count > 0)
            {
                _bottomPos = lows[0].anchoredPosition;
                _step      = lows.Count > 1 ? _bottomPos - lows[1].anchoredPosition : Vector2.zero;
                if (_step.sqrMagnitude < 0.01f)
                    _step = new Vector2(0f, -Math.Max(lows[0].rect.height, 1f));
            }

            _pageUsable = true;
            FruitLog.Info($"[FruitWorldMenu] SCENE page found ({rows.Length} rows, " +
                          $"{(_autoLayout ? "layout group" : "hand-placed rows")}).");
        }

        private static void NoPrototype(string why)
        {
            if (_noPrototypeReported) return;
            _noPrototypeReported = true;
            MelonLogger.Warning($"[FruitWorldMenu] cannot add buttons to the SCENE page ({why}).");
        }

        private static void Create(Def def)
        {
            try
            {
                string name = "FruitLib_WorldButton_" + def.Id;

                // Already there from an earlier pass (the scene holds more than one plate view,
                // and hooks can adopt them in turn): take it back rather than add a second. Its
                // listener is still wired and looks the id up at click time.
                var existing = _rowParent.Find(name);
                if (existing != null)
                {
                    var old = existing.GetComponent<ContextMenuActionButton>();
                    if (old != null) { _built.Add(new Built { Id = def.Id, Clone = old }); return; }
                    UnityEngine.Object.Destroy(existing.gameObject);
                }

                var clone = FruitMenuClone.Make(_proto, _rowParent, name);
                if (clone == null) { MelonLogger.Warning($"[FruitWorldMenu] '{def.Id}': the copy came back null."); return; }

                var button = clone.m_line != null ? clone.m_line.m_button : null;
                if (button == null)
                {
                    UnityEngine.Object.Destroy(clone.gameObject);
                    MelonLogger.Warning($"[FruitWorldMenu] '{def.Id}': the copied row has no UI Button to listen on.");
                    return;
                }

                if (clone.m_line.m_coreServicesProvider == null)
                    FruitLog.Info($"[FruitWorldMenu] '{def.Id}': no core services provider to copy; " +
                                  "the button works but will not animate on hover.");

                // Straight through the Unity button rather than the row's ManagedEvent, for the
                // reason FruitMenuNative gives: the event wants a SingleShotActionsBag to release
                // into, and the only bags around belong to the view. The id is captured, not the
                // Def, so a later AddButton with the same id changes what this click does.
                string id = def.Id;
                Action handler = () => OnClicked(id);
                var listener = (UnityAction)handler;
                button.onClick.AddListener(listener);
                PruneListeners();
                _listeners.Add(new Wired { Clone = clone, Listener = listener });

                // Awake runs here, provider in hand. If the SCENE group is switched off right now
                // it runs when the group next comes on - Draw() waits for that.
                clone.gameObject.SetActive(true);

                _built.Add(new Built { Id = id, Clone = clone });
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitWorldMenu] building '{def.Id}' failed: {e.Message}"); }
        }

        /// <summary>Puts the copies where they belong, in registration order.</summary>
        private static void Layout()
        {
            int k = 0;
            foreach (var d in _defs)
            {
                var b = FindBuilt(d.Id);
                if (b == null || b.Clone == null) continue;
                k++;

                try
                {
                    b.Clone.transform.SetSiblingIndex(_lastRowSibling + k);

                    if (!_autoLayout)
                    {
                        var rt = b.Clone.GetComponent<RectTransform>();
                        if (rt != null) rt.anchoredPosition = _bottomPos + _step * k;
                    }
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitWorldMenu] placing '{d.Id}' failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// Writes each label once the copy is actually awake.
        ///
        /// The word is typed by a ConsoleTextEffect that has to have run its own Awake, which
        /// only happens once the SCENE group is on screen - and the group is off whenever the
        /// player is looking at another World category. So a hidden button is marked undrawn and
        /// written the next time the page is shown, which is when this runs again.
        /// </summary>
        private static void Draw(bool redraw)
        {
            foreach (var d in _defs)
            {
                var b = FindBuilt(d.Id);
                if (b == null || b.Clone == null) continue;

                try
                {
                    if (!b.Clone.gameObject.activeInHierarchy) { b.Drawn = false; continue; }
                    if (b.Drawn && !redraw && b.DrawnLabel == d.Label) continue;

                    b.Clone.Draw(d.Label);
                    b.Drawn      = true;
                    b.DrawnLabel = d.Label;
                }
                catch (Exception e)
                {
                    b.Drawn = false;
                    MelonLogger.Warning($"[FruitWorldMenu] drawing '{d.Id}' failed: {e.Message}");
                }
            }
        }

        private static void OnClicked(string id)
        {
            try
            {
                // The same sound the game gives the rows beside it.
                FruitSfx.PlayUI(UISFXType.SmallButtonClick);

                var def = Find(id);
                if (def?.OnClick == null) return;

                try { def.OnClick(); }
                catch (Exception e) { MelonLogger.Warning($"[FruitWorldMenu] button '{id}' threw: {e}"); }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitWorldMenu] click on '{id}' failed: {e.Message}"); }
        }

        // ── Events ──────────────────────────────────────────────────────────────────────

        internal static void OnSceneRowRan(TerminalSceneRow row)
        {
            var action = (FruitSceneAction)(int)row;

            Raise(SceneActionRan, action);
            if (action == FruitSceneAction.ResetMap || action == FruitSceneAction.ResetAll)
                Raise(MapReset);
        }

        // One subscriber throwing must not stop the rest - same rule as FruitBallistics.

        private static void Raise<T>(Action<T> evt, T arg)
        {
            if (evt == null) return;
            foreach (Delegate d in evt.GetInvocationList())
                try { ((Action<T>)d)(arg); } catch (Exception e) { Report(d, e); }
        }

        private static void Raise(Action evt)
        {
            if (evt == null) return;
            foreach (Delegate d in evt.GetInvocationList())
                try { ((Action)d)(); } catch (Exception e) { Report(d, e); }
        }

        private static readonly HashSet<string> _reported = new HashSet<string>();
        private static void Report(Delegate h, Exception e)
        {
            string who = h.Method.DeclaringType?.FullName + "." + h.Method.Name;
            if (_reported.Add(who)) MelonLogger.Warning($"[FruitWorldMenu] listener {who} threw (reported once): {e}");
        }
    }

    /// <summary>
    /// "This row's action just ran." RunSceneRow is where every one of the six buttons ends up,
    /// once per press; RESET ALL runs its own sub-steps inside it without coming back through
    /// here, so it is one call and one event. Resets happen in place - no scene reload - which
    /// is why this has to be a hook and not an OnSceneWasInitialized.
    /// </summary>
    [HarmonyPatch(typeof(TerminalWorldService), nameof(TerminalWorldService.RunSceneRow))]
    internal static class FruitWorldMenu_RunSceneRowPatch
    {
        static void Postfix(TerminalSceneRow row)
        {
            // An exception escaping a Harmony postfix runs into the game's native code.
            try { FruitWorldMenu.OnSceneRowRan(row); }
            catch (Exception e) { MelonLogger.Warning($"[FruitWorldMenu] scene row hook failed: {e.Message}"); }
        }
    }

    /// <summary>
    /// Builds the buttons whenever the game shows one of its World categories.
    ///
    /// By the time ShowCategory is called the view has woken up (so its serialized row array
    /// is filled in) and, if it is the SCENE group being switched on, the group's children have
    /// been awoken too - which is what the labels need. It also fires again on every category
    /// change, which is how a button hidden behind another category gets its label the moment
    /// it is shown.
    /// </summary>
    [HarmonyPatch(typeof(TerminalWorldPlateView), nameof(TerminalWorldPlateView.ShowCategory))]
    internal static class FruitWorldMenu_ShowCategoryPatch
    {
        static void Postfix(TerminalWorldPlateView __instance)
        {
            try { FruitWorldMenu.Sync(__instance, false); }
            catch (Exception e) { MelonLogger.Warning($"[FruitWorldMenu] category hook failed: {e.Message}"); }
        }
    }

    /// <summary>
    /// Covers the page being switched back on. Closing and reopening the terminal enables the
    /// presenter again without necessarily choosing a category, and text effects may have
    /// been cleared in between, so labels are rewritten.
    /// </summary>
    [HarmonyPatch(typeof(TerminalWorldPresenter), nameof(TerminalWorldPresenter.OnEnable))]
    internal static class FruitWorldMenu_PresenterEnablePatch
    {
        static void Postfix()
        {
            try
            {
                // The presenter's window field is an interface, so the view is found by type. The
                // scene holds more than one, though: stay with the one a category change already
                // showed us, else take one that is actually on.
                var view = FruitWorldMenu.CurrentView;
                if (view == null) view = FruitScene.First<TerminalWorldPlateView>(includeInactive: false);
                if (view != null) FruitWorldMenu.Sync(view, true);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitWorldMenu] page enable hook failed: {e.Message}"); }
        }
    }
}
