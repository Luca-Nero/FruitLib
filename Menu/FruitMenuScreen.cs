using System;
using System.Collections.Generic;
using Il2CppInfrastructure.Project.AssetsHandlers.SFX;
using Il2CppViews.Generic;
using Il2CppViews.Pause;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// FruitLib's corner of the pause menu, and the way through it.
    ///
    /// The menu never changes scene. Every screen is a sibling under Pause/Screens that has
    /// been there since load, and moving between two of them is just their transitions riding
    /// in opposite directions - measured off a running game, the outgoing screen's value and
    /// the incoming one's sum to exactly 1.00 every frame, while the chrome around them does
    /// not move at all. That is the wipe: one edge sweeping across, erasing one screen and
    /// revealing the other behind it. Nothing here reimplements that; it asks the same
    /// transitions to do the same thing.
    ///
    /// Three pages deep at most - mods, that mod's categories, that category's settings -
    /// with the middle one skipped when a mod has only one category, the way the game's own
    /// settings go straight to the resolution table rather than listing one entry.
    ///
    /// <b>The stack is deliberately not involved.</b> PauseScreenId is a fixed enum with no
    /// spare value, so these pages cannot be pushed onto MenuScreenStack and the view will
    /// never route to them. They are left out of m_screens entirely - the switch view ignores
    /// what it does not know about, which is what we want, since we are driving them. The
    /// cost is that the view still believes it is showing Pause the whole time, and the only
    /// way back out is the ESC path FruitLib intercepts.
    /// </summary>
    internal static class FruitMenuScreen
    {
        private static PauseScreen _root;

        private static FruitMenuPage _mods;
        private static FruitMenuPage _cats;
        private static FruitMenuPage _fields;

        /// <summary>Our pages currently up, outermost first. Empty means the pause root is up.</summary>
        private static readonly List<PauseScreen> _open = new List<PauseScreen>();

        private static bool _failed;
        private static int  _waited;

        private const int WaitFrames = 120;

        /// <summary>The mod list exists and can be shown.</summary>
        internal static bool Present => _mods != null;

        /// <summary>One of FruitLib's pages is the thing currently up.</summary>
        internal static bool IsOpen => _open.Count > 0;

        internal static void ResetForScene()
        {
            _root = null;
            _mods = _cats = _fields = null;
            _open.Clear();
            _failed = false;
            _waited = 0;
            ClearRows();
        }

        internal static void Tick(PauseView view)
        {
            if (_mods == null)
            {
                if (_failed || view == null) return;
                Build(view);
                return;
            }

            ListenForKey();
            PollSliders();

            _mods?.TickMarquees();
            _cats?.TickMarquees();
            _fields?.TickMarquees();

            // The whole menu closing takes our pages with it. Without this the next time the
            // player pauses, the game opens RootScreen while one of ours is still riding at
            // 1.00 and both are drawn over each other.
            if (_open.Count > 0 && view != null && !view.IsOpen) Snap();
        }

        // ── Building ─────────────────────────────────────────────────────────────

        private static void Build(PauseView view)
        {
            try
            {
                _root = FindRoot(view);
                if (_root == null)
                {
                    if (++_waited < WaitFrames) return;
                    GiveUp("no RootScreen to ride against");
                    return;
                }

                _mods = FruitMenuPage.Build(view, "ModsScreen", withControls: false);
                if (_mods == null) { GiveUp("the mod list page would not build"); return; }

                FillMods();
            }
            catch (Exception e) { GiveUp(e.Message); }
        }

        /// <summary>
        /// Builds a page the first time it is needed.
        ///
        /// Lazily, because most of the cost of a page is cloning a screen and two controls,
        /// and a player who never opens the mod menu should not pay for any of it.
        /// </summary>
        private static FruitMenuPage Ensure(ref FruitMenuPage page, string name, bool withControls)
        {
            if (page != null) return page;

            var view = FruitMenu.PauseVC;
            if (view == null) return null;

            page = FruitMenuPage.Build(view, name, withControls);
            return page;
        }

        private static PauseScreen FindRoot(PauseView view)
        {
            var screens = view.m_screens;
            if (screens == null) return null;

            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                if (s == null) continue;
                try { if (s.name == "RootScreen") return s.TryCast<PauseScreen>(); }
                catch { }
            }
            return null;
        }

        // ── Filling the pages ────────────────────────────────────────────────────

        private static void FillMods()
        {
            _mods.Clear();

            var names = FruitMenu.ModNames;
            for (int i = 0; i < names.Count; i++)
            {
                int index = i;                       // captured per line, not per loop
                _mods.AddLine(names[i].ToUpperInvariant(), () => OnModPicked(index));
            }

            _mods.SetHeader("pause /", "MODS");
        }

        private static void OnModPicked(int index)
        {
            try
            {
                var categories = FruitMenu.Categories(index);

                // One category is not a choice. Skipping the page that would offer it saves a
                // click that only ever has one answer.
                if (categories.Count <= 1)
                {
                    OpenFields(index, categories.Count == 1 ? categories[0] : null,
                               "pause / mods /", FruitMenu.ModName(index));
                    return;
                }

                var page = Ensure(ref _cats, "ModCatsScreen", withControls: false);
                if (page == null) { FruitMenu.OpenModPanel(index); return; }

                string trail = $"pause / mods / {FruitMenu.ModName(index).ToLowerInvariant()} /";

                page.Clear();
                foreach (var category in categories)
                {
                    string name = category;          // captured per line
                    page.AddLine(name.ToUpperInvariant(), () => OpenFields(index, name, trail, name));
                }

                page.SetHeader("pause / mods /", FruitMenu.ModName(index).ToUpperInvariant());
                Push(page.Screen);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] opening mod {index} failed: {e.Message}"); }
        }

        private static void OpenFields(int index, string category, string trail, string heading)
        {
            try
            {
                var page = Ensure(ref _fields, "ModFieldsScreen", withControls: true);
                if (page == null) { FruitMenu.OpenModPanel(index); return; }

                Bind(page, index, category);
                page.SetHeader(trail, (heading ?? "MOD").ToUpperInvariant());
                Push(page.Screen);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] opening fields for mod {index} failed: {e.Message}"); }
        }

        // ── Rows ─────────────────────────────────────────────────────────────────

        /// <summary>How long a slider must sit still before the change is written to disk.</summary>
        private const float SettleSeconds = 0.3f;

        private sealed class Row
        {
            internal FruitField    Field;
            internal SettingSlider Slider;
            internal float         LastSeen;
        }

        private static readonly List<Row> _rows = new List<Row>();

        /// <summary>The key bindings on the page, in the order their table rows sit.</summary>
        private static readonly List<FruitField>       _keys     = new List<FruitField>();
        private static readonly List<SettingTableLine> _keyLines = new List<SettingTableLine>();

        /// <summary>Which binding is waiting for a key, or -1.</summary>
        private static int _listening = -1;
        private static int   _rowsMod = -1;
        private static bool  _dirty;
        private static float _lastChange;

        private static void ClearRows()
        {
            _rows.Clear();
            _keys.Clear();
            _keyLines.Clear();
            _listening = -1;
            _rowsMod   = -1;
            _dirty     = false;
        }

        private static void Bind(FruitMenuPage page, int index, string category)
        {
            page.Clear();
            ClearRows();
            _rowsMod = index;

            foreach (var field in FruitMenu.NativeFields(index, category))
            {
                if (field.Kind == FruitFieldKind.Bool)
                {
                    var           captured = field;
                    SettingToggle toggle   = null;
                    toggle = page.AddToggle(field.Label, field.GetBool(), () => Flip(captured, toggle));
                    continue;
                }

                if (field.Kind == FruitFieldKind.Key) { _keys.Add(field); continue; }

                float value  = Mathf.Clamp(field.GetNumber(), field.Min, field.Max);
                var   slider = page.AddSlider(field.Label, field.Min, field.Max, value);
                if (slider != null) _rows.Add(new Row { Field = field, Slider = slider, LastSeen = value });
            }

            if (_keys.Count > 0)
            {
                var actions = new List<string>(_keys.Count);
                foreach (var key in _keys) actions.Add(key.Label);

                List<SettingTableLine> built;
                page.AddKeyTable(new[] { "ACTION", "KEY" }, actions,
                                 i => KeyName(_keys[i].GetKey()), StartListening, out built);
                _keyLines.AddRange(built);
            }

            // The way through to everything this page cannot draw: key bindings, free text,
            // and the momentary bools that are really action buttons.
            page.AddLine("ALL SETTINGS", () => FruitMenu.OpenModPanel(index));

            MelonLogger.Msg($"[FruitMenu] '{FruitMenu.ModName(index)}'" +
                            (category != null ? $" / {category}" : "") +
                            $": {_rows.Count} slider(s), {_keys.Count} binding(s).");
        }

        /// <summary>A key's name as the table shows it.</summary>
        private static string KeyName(KeyCode key) => key == KeyCode.None ? "none" : key.ToString();

        /// <summary>
        /// Starts waiting for a key for one binding.
        ///
        /// The table's chosen-row highlight is the whole of the "press a key" state. It is
        /// what the resolution picker uses to show which row you are on, and it says the same
        /// thing here without anything extra having to be drawn.
        /// </summary>
        private static void StartListening(int index)
        {
            if (index < 0 || index >= _keys.Count) return;

            Paint(_listening, false);
            _listening = index;
            Paint(_listening, true);
        }

        internal static void CancelListening()
        {
            if (_listening < 0) return;

            Paint(_listening, false);
            _listening = -1;
        }

        /// <summary>Whether a binding is waiting, so ESC can cancel instead of navigating.</summary>
        internal static bool Listening => _listening >= 0;

        private static void Paint(int index, bool chosen)
        {
            if (index < 0 || index >= _keyLines.Count) return;
            try { _keyLines[index].SetChosen(chosen); } catch { }
        }

        /// <summary>
        /// Takes the next key pressed and binds it.
        ///
        /// Every KeyCode is tried rather than reading typed characters, because a binding is
        /// about the physical key - F8 and Mouse3 have no character to report. It is a few
        /// hundred comparisons, and only while a row is waiting.
        /// </summary>
        private static void ListenForKey()
        {
            if (_listening < 0 || !Input.anyKeyDown) return;

            try
            {
                foreach (var value in Enum.GetValues(typeof(KeyCode)))
                {
                    var key = (KeyCode)value;
                    if (key == KeyCode.None || !Input.GetKeyDown(key)) continue;

                    var field = _keys[_listening];
                    field.SetKey(key);
                    WriteKeyCell(page: _fields, index: _listening, key: key);
                    MelonLogger.Msg($"[FruitMenu] '{field.Label}' bound to {key}.");

                    CancelListening();

                    // Written at once rather than left to settle. A binding is one deliberate
                    // act, not a value being dragged towards something.
                    Touch();
                    Save();
                    return;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitMenu] binding a key failed: {e.Message}");
                CancelListening();
            }
        }

        private static void WriteKeyCell(FruitMenuPage page, int index, KeyCode key)
        {
            if (page == null || index < 0 || index >= _keyLines.Count) return;
            try { page.WriteCells(_keyLines[index].m_cells, new[] { _keys[index].Label, KeyName(key) }); }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] redrawing a binding failed: {e.Message}"); }
        }

        private static void Flip(FruitField field, SettingToggle toggle)
        {
            try
            {
                bool next = !field.GetBool();
                field.SetBool(next);
                if (toggle != null) toggle.SetOn(next);

                FruitSfx.PlayUI(next ? UISFXType.SwitchOn : UISFXType.SwitchOff);
                Touch();
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] toggling '{field.Label}' failed: {e.Message}"); }
        }

        /// <summary>
        /// Reads the sliders back.
        ///
        /// Polled rather than subscribed: SettingSlider reports through an IManagedEvent, and
        /// subscribing to one needs a SingleShotActionsBag to release into - the same problem
        /// the MODS button had. A handful of float comparisons a frame is a far smaller thing
        /// than borrowing a bag whose firing schedule belongs to someone else.
        /// </summary>
        private static void PollSliders()
        {
            if (_rows.Count == 0) return;

            try
            {
                foreach (var row in _rows)
                {
                    var core = row.Slider != null ? row.Slider.m_slider : null;
                    if (core == null) continue;

                    float now = core.Value;
                    if (Mathf.Approximately(now, row.LastSeen)) continue;

                    row.LastSeen = now;
                    row.Field.SetNumber(now);
                    Touch();
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] reading the sliders failed: {e.Message}"); }

            // Settled, so write. Saving on every frame of a drag would rewrite the ini a
            // hundred times for one adjustment, and every write tells the owning mod its
            // config changed.
            if (_dirty && Time.unscaledTime - _lastChange > SettleSeconds) Save();
        }

        private static void Touch()
        {
            _dirty      = true;
            _lastChange = Time.unscaledTime;
        }

        private static void Save()
        {
            _dirty = false;
            try { FruitMenu.SaveMod(_rowsMod); }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] saving mod {_rowsMod} failed: {e.Message}"); }
        }

        // ── Riding ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Swaps one screen for another the way the game does: both transitions moving in the
        /// same frame, chrome untouched.
        ///
        /// The incoming screen opens first on purpose. Both calls land in the same frame so it
        /// looks no different either way, but if the incoming one throws the outgoing one is
        /// still standing and the menu still works. The other order is how a missing ride
        /// timing turned into an empty pause screen with no way back out of it.
        /// </summary>
        private static bool Ride(PauseScreen outgoing, PauseScreen incoming, bool rewinds)
        {
            if (outgoing == null || incoming == null) return false;

            FruitMenuClone.ClearHover(outgoing.gameObject);

            try { incoming.Open(rewinds); }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitMenu] opening '{incoming.name}' failed, leaving '{outgoing.name}' up: {e.Message}");
                return false;
            }

            try { outgoing.Close(rewinds); }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] closing '{outgoing.name}' failed: {e.Message}"); }

            return true;
        }

        private static PauseScreen Top => _open.Count > 0 ? _open[_open.Count - 1] : _root;

        private static void Push(PauseScreen screen)
        {
            if (screen == null || screen == Top) return;
            if (Ride(Top, screen, false)) _open.Add(screen);
        }

        /// <summary>Pause root to the mod list.</summary>
        internal static void Show()
        {
            if (_mods == null || _root == null || _open.Count > 0) return;

            FillMods();          // a mod may have registered since this was last up
            Push(_mods.Screen);
        }

        /// <summary>One page back out, for ESC to call.</summary>
        internal static void Back()
        {
            if (_open.Count == 0) return;

            CancelListening();
            if (_dirty) Save();   // whatever the sliders were left on

            var leaving = _open[_open.Count - 1];
            var under   = _open.Count >= 2 ? _open[_open.Count - 2] : _root;

            if (Ride(leaving, under, true)) _open.RemoveAt(_open.Count - 1);
        }

        /// <summary>Hidden at once, no ride. For when the whole menu has gone away under us.</summary>
        private static void Snap()
        {
            if (_dirty) Save();

            foreach (var screen in _open)
            {
                if (screen == null) continue;
                try { screen.Erase(); } catch { }
            }
            _open.Clear();
        }

        private static void GiveUp(string reason)
        {
            _failed = true;
            MelonLogger.Warning($"[FruitMenu] could not build the mod pages ({reason}); " +
                                "the MODS button opens the IMGUI panel instead.");
        }
    }
}
