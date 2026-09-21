using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInfrastructure.Project.AssetsHandlers.SFX;
using Il2CppTMPro;
using Il2CppViews.Generic;
using Il2CppViews.Pause;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;

namespace FruitLib
{
    /// <summary>
    /// A pause-menu page of FruitLib's own, built from the game's SETTINGS screen.
    ///
    /// Every page in this menu is one column - a list of lines to go deeper into, or a set of
    /// rows to change things on - so one class covers the lot. SETTINGS is the copy of choice
    /// because its chrome is the scrolling kind; a mod can easily have more fields than fit,
    /// and a page that silently cuts off at the bottom of the screen is worse than an ugly one.
    ///
    /// A page is built once and refilled, because screens are not cheap - each carries a
    /// transition, a chrome and a header - and only one is ever up.
    /// </summary>
    internal sealed class FruitMenuPage
    {
        /// <summary>Our own objects are named so they can be told apart from the game's.</summary>
        internal const string Prefix = "FruitLib_";

        internal PauseScreen Screen { get; private set; }

        private Transform      _column;
        private PathHeaderView _header;

        private MenuLineButton _lineProto;
        private SettingToggle  _toggleProto;
        private SettingSlider  _sliderProto;
        private SettingTable   _tableProto;

        private readonly List<GameObject>  _built    = new List<GameObject>();
        private readonly List<UnityAction> _handlers = new List<UnityAction>();
        private readonly List<Marquee>     _marquees = new List<Marquee>();

        internal bool CanDrawControls => _toggleProto != null && _sliderProto != null;

        /// <summary>
        /// A new page, or null if the menu is not shaped the way this expects.
        ///
        /// <paramref name="withControls"/> borrows a toggle and a slider from elsewhere in the
        /// menu. Only a page that shows settings needs them, and borrowing costs two clones,
        /// so a page of plain lines does without.
        /// </summary>
        internal static FruitMenuPage Build(PauseView view, string name, bool withControls)
        {
            try
            {
                var source = FindScreen(view, "SettingsScreen");
                if (source == null) { MelonLogger.Warning($"[FruitMenu] {name}: no SettingsScreen to copy."); return null; }

                var timings = source.Timings;
                if (timings == null) { MelonLogger.Warning($"[FruitMenu] {name}: SettingsScreen has no ride timings."); return null; }

                var clone = FruitMenuClone.Make(source, source.transform.parent, Prefix + name);
                if (clone == null) return null;

                clone.gameObject.SetActive(true);
                clone.SetRideTimings(timings);

                // Read back rather than trust the call. A page that cannot be opened is worse
                // than no page: whatever rides it out would close and leave nothing behind.
                try { var _ = clone.Timings.ToString(); }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[FruitMenu] {name}: the copy has no usable timings ({e.Message}).");
                    try { UnityEngine.Object.Destroy(clone.gameObject); } catch { }
                    return null;
                }

                try { clone.Erase(); } catch { }

                var lines = clone.GetComponentsInChildren<MenuLineButton>(true);
                if (lines == null || lines.Length == 0)
                {
                    MelonLogger.Warning($"[FruitMenu] {name}: the copy has no line button to use as a prototype.");
                    try { UnityEngine.Object.Destroy(clone.gameObject); } catch { }
                    return null;
                }

                var page = new FruitMenuPage
                {
                    Screen     = clone,
                    _lineProto = lines[0],
                    _column    = lines[0].transform.parent,
                    _header    = clone.GetComponentInChildren<PathHeaderView>(true),
                };

                // The copied SETTINGS entries are switched off rather than destroyed; the
                // first of them stays as the prototype every line on this page is cut from.
                foreach (var line in lines)
                    if (line != null) line.gameObject.SetActive(false);

                if (withControls)
                {
                    page._toggleProto = page.Borrow<SettingToggle>(view, "ToggleProto");
                    page._sliderProto = page.Borrow<SettingSlider>(view, "SliderProto");
                    page._tableProto  = page.Borrow<SettingTable>(view, "TableProto");
                }

                MelonLogger.Msg($"[FruitMenu] page '{name}' built" +
                                (withControls
                                    ? $" (toggle={(page._toggleProto != null)}, slider={(page._sliderProto != null)}, " +
                                      $"table={(page._tableProto != null)})"
                                    : ""));
                return page;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] {name}: build failed: {e}"); return null; }
        }

        /// <summary>
        /// Parks an inactive copy of the first <typeparamref name="T"/> the game owns in our column.
        ///
        /// Ours are skipped by name. By the time a second page is built the first one's
        /// prototypes and rows are under the menu too, and a copy taken from those would
        /// inherit whatever had already been done to them.
        /// </summary>
        private T Borrow<T>(PauseView view, string name) where T : Component
        {
            try
            {
                var found = view.GetComponentsInChildren<T>(true);
                if (found == null) return null;

                foreach (var candidate in found)
                {
                    if (candidate == null || candidate.gameObject.name.StartsWith(Prefix)) continue;

                    var copy = FruitMenuClone.Make(candidate, _column, Prefix + name);
                    if (copy == null) continue;

                    copy.gameObject.SetActive(false);
                    return copy;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] no {typeof(T).Name} to borrow: {e.Message}"); }
            return null;
        }

        // ── Filling in ───────────────────────────────────────────────────────────

        internal void Clear()
        {
            foreach (var go in _built)
            {
                if (go == null) continue;

                // Deactivated before being destroyed. Destroy is deferred to the end of the
                // frame and a layout group counts a dying child until then, so the new
                // contents would be laid out around the old ones for a frame.
                go.SetActive(false);
                UnityEngine.Object.Destroy(go);
            }

            _built.Clear();
            _handlers.Clear();
            _marquees.Clear();
        }

        internal void AddLine(string word, Action onClick)
        {
            if (_lineProto == null) return;
            try
            {
                var line = FruitMenuClone.Make(_lineProto, _column, Prefix + "Line_" + word);
                if (line == null) return;

                line.gameObject.SetActive(true);
                line.SetWord(word);
                _built.Add(line.gameObject);

                var button = line.m_button;
                if (button == null) return;

                // Cleared rather than added to. Whatever the page this was copied from had
                // wired into onClick came along with the copy, and a mod line that also opened
                // the real Graphics screen would be a miserable thing to debug. Clearing takes
                // the button's own listener too - the one that plays the press - so the
                // handler raises it.
                button.onClick.RemoveAllListeners();

                UnityAction handler = (Action)(() =>
                {
                    try { line.RaiseClicked(); } catch { }
                    FruitSfx.PlayUI(UISFXType.LargeButtonClick);
                    onClick?.Invoke();
                });
                button.onClick.AddListener(handler);
                _handlers.Add(handler);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] line '{word}' failed: {e.Message}"); }
        }

        internal SettingToggle AddToggle(string label, bool on, Action onClick)
        {
            if (_toggleProto == null) return null;
            try
            {
                var toggle = FruitMenuClone.Make(_toggleProto, _column, Prefix + "Row_" + label);
                if (toggle == null) return null;

                toggle.gameObject.SetActive(true);
                toggle.SetLabel(label);
                Fit(toggle.m_label, label);
                toggle.SetOn(on);
                _built.Add(toggle.gameObject);

                var button = toggle.m_button;
                if (button == null) return toggle;

                button.onClick.RemoveAllListeners();

                UnityAction handler = (Action)(() =>
                {
                    try { toggle.RaiseClicked(); } catch { }
                    onClick?.Invoke();
                });
                button.onClick.AddListener(handler);
                _handlers.Add(handler);

                return toggle;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] toggle '{label}' failed: {e.Message}"); return null; }
        }

        internal SettingSlider AddSlider(string label, float min, float max, float value)
        {
            if (_sliderProto == null) return null;
            try
            {
                var slider = FruitMenuClone.Make(_sliderProto, _column, Prefix + "Row_" + label);
                if (slider == null) return null;

                slider.gameObject.SetActive(true);
                slider.SetLabel(label);
                Fit(slider.m_label, label);
                slider.ConfigureValue(min, max, value);
                _built.Add(slider.gameObject);

                return slider;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] slider '{label}' failed: {e.Message}"); return null; }
        }

        /// <summary>
        /// A table of key bindings, built from the one the resolution picker uses.
        ///
        /// SettingTable is the game's widget for "here is a list, pick one", which is exactly
        /// the shape of a keybind list - and its chosen-row highlight doubles as the "waiting
        /// for a key" state, so nothing has to be drawn for that either.
        ///
        /// The rows are built by cloning the table's own line prefab and writing into the
        /// cells, rather than through SetLines. SetLines wants a nested il2cpp list, and
        /// building one of those from this side needs a generic instantiation that may not
        /// have been compiled in; the cells are a plain array of labels on a non-generic
        /// class, which cannot go wrong the same way.
        /// </summary>
        internal SettingTable AddKeyTable(IReadOnlyList<string> headers,
                                          IReadOnlyList<string> actions,
                                          Func<int, string> keyOf,
                                          Action<int> onPicked,
                                          out List<SettingTableLine> lines)
        {
            lines = new List<SettingTableLine>();
            if (_tableProto == null || actions.Count == 0) return null;

            try
            {
                var table = FruitMenuClone.Make(_tableProto, _column, Prefix + "KeyTable");
                if (table == null) return null;

                // The factory is what the table builds its own lines with. We build ours, but
                // anything it does internally - a rebuild, a clear - would trip over a null.
                try { table.m_monoFactory = _tableProto.m_monoFactory; } catch { }

                table.gameObject.SetActive(true);
                _built.Add(table.gameObject);

                WriteCells(table.m_headerCells, headers);

                var prefab = table.m_linePrefab;
                var pivot  = table.m_linesPivot;
                if (prefab == null || pivot == null)
                {
                    MelonLogger.Warning("[FruitMenu] the copied table has no line prefab to build from.");
                    return table;
                }

                // Any rows the copy brought with it are the resolutions it was showing.
                foreach (var stale in table.GetComponentsInChildren<SettingTableLine>(true))
                    if (stale != null) stale.gameObject.SetActive(false);

                for (int i = 0; i < actions.Count; i++)
                {
                    var line = FruitMenuClone.Make(prefab, pivot, Prefix + "Key_" + actions[i]);
                    if (line == null) continue;

                    line.gameObject.SetActive(true);
                    WriteCells(line.m_cells, new[] { actions[i], keyOf(i) });
                    lines.Add(line);

                    int index  = i;                  // captured per row
                    var button = line.m_button;
                    if (button == null) continue;

                    button.onClick.RemoveAllListeners();

                    UnityAction handler = (Action)(() =>
                    {
                        FruitSfx.PlayUI(UISFXType.SmallButtonClick);
                        onPicked?.Invoke(index);
                    });
                    button.onClick.AddListener(handler);
                    _handlers.Add(handler);
                }

                // The game's own fix-up for this table: measure every last cell and give them
                // all the widest one's width. It was written for resolutions, which are all
                // much of a length; action names are not, so it matters more here.
                try { table.ShareTheWidestLastCell(); }
                catch (Exception e) { MelonLogger.Warning($"[FruitMenu] evening up the key column failed: {e.Message}"); }

                MelonLogger.Msg($"[FruitMenu] key table: {lines.Count} row(s), " +
                                $"{(lines.Count > 0 ? lines[0].CellCount : 0)} cell(s) per row.");
                return table;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] key table failed: {e.Message}"); return null; }
        }

        /// <summary>
        /// Lays words into a table row's cells, against the ends of the row.
        ///
        /// The row was authored for the resolution picker and carries three cells where a
        /// key binding needs two. The first word goes in the first cell and the last word in
        /// the last, because ShareTheWidestLastCell - the table's own way of squaring its
        /// columns up - measures the last cell of every row. Putting the key anywhere else
        /// leaves it out of the only alignment the widget offers.
        ///
        /// Spare cells in between are switched off rather than emptied. An empty cell is
        /// still a cell as far as the layout is concerned, and keeps its share of the width.
        /// </summary>
        internal void WriteCells(Il2CppReferenceArray<TextMeshProUGUI> cells,
                                 IReadOnlyList<string> words)
        {
            if (cells == null || cells.Length == 0 || words == null) return;

            int cellCount = cells.Length;
            int wordCount = words.Count;

            for (int i = 0; i < cellCount; i++)
            {
                var cell = cells[i];
                if (cell == null) continue;

                string text;
                if (wordCount >= cellCount)     text = i < wordCount ? words[i] : null;
                else if (wordCount == 1)        text = i == 0 ? words[0] : null;
                else if (i < wordCount - 1)     text = words[i];
                else if (i == cellCount - 1)    text = words[wordCount - 1];
                else                            text = null;

                try
                {
                    bool used = text != null;
                    if (cell.gameObject.activeSelf != used) cell.gameObject.SetActive(used);
                    if (used) Fit(cell, text);
                }
                catch { }
            }
        }

        /// <summary>How long each character of a scrolling label is held.</summary>
        private const float MarqueeStep = 0.18f;

        /// <summary>Put between the end of a scrolling label and its start coming round again.</summary>
        private const string MarqueeGap = "   ";

        private sealed class Marquee
        {
            internal TextMeshProUGUI Label;
            internal string          Full;
            internal string          Loop;
            internal int             Window = -1;   // -1 until the layout has a width to measure
            internal int             Offset;
            internal float           NextStep;
        }

        /// <summary>
        /// Puts a label in its box, and scrolls it there if it does not fit.
        ///
        /// TMP draws past the edge of its own rect rather than stopping at it, and these rows
        /// are laid out in columns - so a long name is not pushed aside by the value next to
        /// it, it is drawn straight over the top. The game never hit this because it authored
        /// every string that goes through these rows; a mod's are whatever the mod calls them.
        ///
        /// Ellipsis is set as well as the scroll, because it is what shows for the frame or
        /// two before the layout has a width to measure against, and what stays if the label
        /// turns out to be unmeasurable.
        /// </summary>
        internal void Fit(TextMeshProUGUI label, string text)
        {
            if (label == null) return;

            try
            {
                label.enableWordWrapping = false;
                label.overflowMode       = TextOverflowModes.Ellipsis;
                label.text               = text;

                if (!string.IsNullOrEmpty(text))
                    _marquees.Add(new Marquee { Label = label, Full = text });
            }
            catch { }
        }

        /// <summary>
        /// Advances every scrolling label on this page.
        ///
        /// Character by character rather than by sliding the transform: the menu is set in a
        /// monospaced console face, so a window onto the string moves exactly one cell at a
        /// time and needs no mask to clip it. It also reads like the rest of the game.
        /// </summary>
        internal void TickMarquees()
        {
            if (_marquees.Count == 0) return;

            float now = Time.unscaledTime;

            foreach (var m in _marquees)
            {
                if (m.Label == null) continue;

                // Measured on a later frame than it was built. A row that was cloned this
                // frame has not been through a layout pass yet, so its rect is still zero
                // wide and anything measured against it would be nonsense.
                if (m.Window < 0) { Measure(m); continue; }

                if (m.Window == 0 || now < m.NextStep) continue;   // fits, or not due yet

                m.NextStep = now + MarqueeStep;
                m.Offset   = (m.Offset + 1) % m.Loop.Length;

                try { m.Label.text = Slice(m); } catch { }
            }
        }

        private static void Measure(Marquee m)
        {
            try
            {
                float available = m.Label.rectTransform.rect.width;
                if (available <= 1f) return;                 // layout has not run yet

                float full = m.Label.GetPreferredValues(m.Full).x;
                if (full <= available) { m.Window = 0; return; }   // fits as it is

                // Monospaced, so one character is the whole string's width over its length.
                float character = full / Mathf.Max(1, m.Full.Length);
                int   fits      = Mathf.FloorToInt(available / Mathf.Max(0.01f, character));

                m.Window = Mathf.Max(3, fits - 1);
                m.Loop   = m.Full + MarqueeGap;
                m.Label.text = Slice(m);
            }
            catch { m.Window = 0; }
        }

        private static string Slice(Marquee m)
        {
            var sb = new StringBuilder(m.Window);
            for (int i = 0; i < m.Window; i++)
                sb.Append(m.Loop[(m.Offset + i) % m.Loop.Length]);
            return sb.ToString();
        }

        /// <summary>
        /// Writes the breadcrumb straight into its two labels.
        ///
        /// Not through PathHeaderView.Draw: the breadcrumb is normally redrawn by the switch
        /// view off the screen stack, and these pages are not in the stack, so nothing will
        /// ever overwrite what is set here.
        /// </summary>
        internal void SetHeader(string trail, string current)
        {
            try
            {
                if (_header == null) return;
                if (_header.m_trail   != null) _header.m_trail.text   = trail;
                if (_header.m_current != null) _header.m_current.text = current;
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] header failed: {e.Message}"); }
        }

        private static PauseScreen FindScreen(PauseView view, string name)
        {
            var screens = view.m_screens;
            if (screens == null) return null;

            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                if (s == null) continue;
                try { if (s.name == name) return s.TryCast<PauseScreen>(); }
                catch { }
            }
            return null;
        }
    }
}
