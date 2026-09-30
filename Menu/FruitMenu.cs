using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppViews.Pause;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    [AttributeUsage(AttributeTargets.Field)]
    public class MenuCategoryAttribute : Attribute
    {
        public readonly string Name;
        public MenuCategoryAttribute(string name) => Name = name;
    }

    public enum ButtonKind { Toggle, Momentary }

    /// <summary>
    /// What to call a setting in the menu, when the field name is not what you would say
    /// out loud.
    ///
    /// The field name still keys the ini file, so adding, changing or removing a label never
    /// touches anyone's saved config.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class MenuLabelAttribute : Attribute
    {
        public readonly string Text;
        public MenuLabelAttribute(string text) => Text = text;
    }

    /// <summary>
    /// The range a numeric field may take, so the mod menu can draw it as a slider.
    ///
    /// Without one FruitLib has to guess from the field's default, and a guess is usually
    /// wrong in the direction that matters - a value you cannot reach. Declaring it costs a
    /// line and is the difference between a slider that works and one that annoys.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class MenuRangeAttribute : Attribute
    {
        public readonly float Min, Max;
        public MenuRangeAttribute(float min, float max) { Min = min; Max = max; }
    }

    internal enum FruitFieldKind { Bool, Number, Key }

    /// <summary>One setting, described in the terms a native control needs.</summary>
    internal sealed class FruitField
    {
        internal FruitSetting    Setting;
        internal string          Label;
        internal FruitFieldKind  Kind;
        internal bool            IsInt;
        internal float           Min, Max;
        internal bool            RangeDeclared;

        internal bool  GetBool()        => (bool)Setting.Value;
        internal void  SetBool(bool v)  => Setting.Value = v;

        internal float GetNumber() => IsInt ? (int)Setting.Value : (float)Setting.Value;

        internal KeyCode GetKey()          => (KeyCode)Setting.Value;
        internal void    SetKey(KeyCode k) => Setting.Value = k;

        internal void SetNumber(float v)
        {
            if (IsInt) Setting.Value = Mathf.RoundToInt(v);
            else       Setting.Value = v;
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class MenuButtonAttribute : Attribute
    {
        public readonly ButtonKind Kind;
        public MenuButtonAttribute(ButtonKind kind) => Kind = kind;
    }

    public static class FruitMenu
    {
        // ── State ─────────────────────────────────────────────────────────────
        private enum MenuState { Closed, Button, Settings }
        private static MenuState _state = MenuState.Closed;
        public static bool IsOpen  => _state != MenuState.Closed;

        public static event Action OnConfigChanged;

        public static bool JustClosed         { get; internal set; }
        /// <summary>
        /// A menu has the mouse and keyboard: the FruitLib menu, or one of the game's own (the
        /// terminal, a context menu), or one of them closed this frame or the last. Gate item
        /// use and hotkeys on this. The game's menus count since 5.5.0.
        /// </summary>
        public static bool IsInputSuppressed  => IsOpen || JustClosed || GameMenuOpen || FruitGameMenus.JustClosed;

        /// <summary>One of the game's own menus is open: the terminal (items / world), a context
        /// menu, anything the game's tools themselves stand down for (5.5.0).</summary>
        public static bool GameMenuOpen => FruitGameMenus.AnyOpen;

        public static bool IsGamePaused { get; private set; }

        /// <summary>Convenience: paused, or in/just out of the FruitLib menu.</summary>
        public static bool BlocksGameplayInput => IsGamePaused || IsInputSuppressed;

        private static readonly List<GameObject> _hidden = new List<GameObject>();
        internal static PauseView PauseVC;

        // ── Mod registry ──────────────────────────────────────────────────────
        private static readonly List<ModEntry> _mods = new List<ModEntry>();
        private static int _selectedMod;

        /// <summary>What to call a field: its MenuLabel if it has one, otherwise its name.</summary>
        internal static string LabelOf(FieldInfo f)
        {
            var label = (MenuLabelAttribute)Attribute.GetCustomAttribute(f, typeof(MenuLabelAttribute));
            return string.IsNullOrEmpty(label?.Text) ? f.Name : label.Text;
        }

        /// <summary>
        /// Lists a mod's config class in the MODS menu. When a value changes in-game FruitLib
        /// writes <paramref name="iniFilePath"/> as a flat key = value list; prefer the overload
        /// that takes the mod's own writer, which keeps its sections and help text.
        /// </summary>
        public static void Register(string displayName, string iniFilePath, Type configType)
            => Register(displayName, iniFilePath, configType, null);

        /// <summary>
        /// Lists a mod's config class in the MODS menu, saved through <paramref name="save"/>
        /// when a value changes in-game, so the ini has one writer: the mod's own. Null falls
        /// back to FruitLib's flat write.
        /// </summary>
        public static void Register(string displayName, string iniFilePath, Type configType, Action save)
        {
            _mods.Add(new ModEntry(displayName, iniFilePath, configType, save));
            FruitLog.Info($"[FruitLib] Registered: {displayName}");
        }

        /// <summary>
        /// Remembers what <paramref name="configType"/>'s settings hold right now, as the values
        /// Reset to Defaults goes back to. Call it before the mod loads its ini: Register runs
        /// after that load, so without it the "defaults" it sees are the player's own values.
        /// Mods that never call it keep the old behaviour. Repeat calls for a type are ignored.
        /// </summary>
        public static void CaptureDefaults(Type configType) => FruitSetting.CaptureDefaults(configType);

        // ── State transitions ─────────────────────────────────────────────────
        internal static void OnPauseStateChanged(bool isPaused)
        {
            IsGamePaused = isPaused;
            if (isPaused  && _state == MenuState.Closed) _state = MenuState.Button;
            if (!isPaused && _state == MenuState.Button)  _state = MenuState.Closed;
        }

        internal static void OnGameResumed()
        {
            ModEntry.CancelEdit();
            RestoreNativeUI();
            _state = MenuState.Closed;
        }

        /// <summary>Opens the mod settings, for the native pause-menu button to call.</summary>
        internal static void OpenFromNativeButton() => OpenSettings();

        /// <summary>Whether the mod settings panel is the thing currently on screen.</summary>
        internal static bool IsPanelOpen => _state == MenuState.Settings;

        /// <summary>
        /// Picks up MelonPreferences categories created since the last look, so mods that
        /// never call <see cref="Register(string, string, Type, Action)"/> still get a page. They list after the FruitLib
        /// mods, one entry per category.
        /// </summary>
        internal static void SyncPreferences() =>
            FruitPreferencesBridge.Sync((name, settings, save) => _mods.Add(new ModEntry(name, settings, save)));

        /// <summary>Registered mods, in the order the MODS screen lists them.</summary>
        internal static IReadOnlyList<string> ModNames
        {
            get
            {
                SyncPreferences();
                var names = new List<string>(_mods.Count);
                foreach (var m in _mods) names.Add(m.DisplayName);
                return names;
            }
        }

        /// <summary>
        /// The fields of one mod that a native control can represent.
        ///
        /// Bools become toggles, numbers become sliders, key bindings become rows in a table.
        /// What is left - free text, and the momentary bools that are really action buttons -
        /// has no equivalent in this game's settings UI, so it stays reachable through the
        /// IMGUI panel instead.
        /// </summary>
        internal static List<FruitField> NativeFields(int index, string category = null)
        {
            var result = new List<FruitField>();
            if (index < 0 || index >= _mods.Count) return result;

            _mods[index].DescribeFields(result, category);
            return result;
        }

        /// <summary>
        /// One mod's categories, in declaration order.
        ///
        /// A mod with one category gets no category page - there would be nothing to choose -
        /// so its fields open directly, the way the game's own settings skip straight to the
        /// resolution table rather than listing one entry.
        /// </summary>
        internal static List<string> Categories(int index) =>
            index >= 0 && index < _mods.Count ? _mods[index].Categories() : new List<string>();

        /// <summary>Writes one mod's config back to disk and tells its owner.</summary>
        internal static void SaveMod(int index)
        {
            if (index >= 0 && index < _mods.Count) _mods[index].Save();
        }

        /// <summary>The display name of one mod.</summary>
        internal static string ModName(int index) =>
            index >= 0 && index < _mods.Count ? _mods[index].DisplayName : "MOD";

        /// <summary>Opens the panel on one mod, for a MODS screen line to call.</summary>
        internal static void OpenModPanel(int index)
        {
            if (index >= 0 && index < _mods.Count) _selectedMod = index;

            // The panel deactivates the pause view, which is how a button that was under the
            // pointer at the time ends up still lit when it comes back.
            if (PauseVC != null) FruitMenuClone.ClearHover(PauseVC.gameObject);

            OpenSettings();
        }

        /// <summary>Closes the panel back to the pause root, for ESC to call.</summary>
        internal static void StepBackFromPanel() => CloseSettings();

        private static void OpenSettings()
        {
            SyncPreferences();
            _hidden.Clear();
            if (PauseVC != null)
            {
                var t = PauseVC.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i).gameObject;
                    if (child.name == "BlurBg" || !child.activeSelf) continue;
                    child.SetActive(false);
                    _hidden.Add(child);
                }
            }
            _state = MenuState.Settings;
        }

        private static void CloseSettings()
        {
            ModEntry.CancelEdit();
            RestoreNativeUI();
            _state = MenuState.Button;
        }

        private static void RestoreNativeUI()
        {
            foreach (var go in _hidden) if (go != null) go.SetActive(true);
            _hidden.Clear();
        }

        // ── Responsive layout ─────────────────────────────────────────────────
        private static float Scale   => Screen.height * 0.75f / 520f;
        private static float W       => 620f * Scale;
        private static float H       => Screen.height * 0.75f;
        private static float PAD     => 10f * Scale;
        private static float MOD_TH  => 26f * Scale;
        private static float CAT_TH  => 22f * Scale;
        private static float CAT_GAP => 3f  * Scale;
        private static float ROW     => 24f * Scale;
        private static float RGAP    => 6f  * Scale;

        private static float HeaderH(int catRows) =>
            PAD + MOD_TH + 4f * Scale
            + catRows * CAT_TH + (catRows > 1 ? CAT_GAP : 0f)
            + 8f * Scale;

        // ── Colours ───────────────────────────────────────────────────────────
        private static readonly Color C_Panel   = new Color(0.10f, 0.10f, 0.10f, 0.97f);
        private static readonly Color C_Header  = new Color(0.07f, 0.07f, 0.07f, 1.00f);
        private static readonly Color C_Divide  = new Color(0.28f, 0.28f, 0.28f, 1.00f);
        private static readonly Color C_Content = new Color(0.12f, 0.12f, 0.12f, 1.00f);
        private static readonly Color C_TabN    = new Color(0.22f, 0.22f, 0.22f, 1.00f);
        private static readonly Color C_ModSel  = new Color(0.32f, 0.32f, 0.32f, 1.00f);
        private static readonly Color C_CatSel  = new Color(0.14f, 0.36f, 0.56f, 1.00f);
        private static readonly Color C_RowAlt  = new Color(0.16f, 0.16f, 0.16f, 1.00f);
        private static readonly Color C_Plus    = new Color(0.16f, 0.38f, 0.16f, 1.00f);
        private static readonly Color C_Minus   = new Color(0.38f, 0.16f, 0.16f, 1.00f);
        private static readonly Color C_BoolOn  = new Color(0.16f, 0.38f, 0.16f, 1.00f);
        private static readonly Color C_BoolOff = new Color(0.30f, 0.30f, 0.30f, 1.00f);
        private static readonly Color C_Action  = new Color(0.36f, 0.22f, 0.08f, 1.00f);
        private static readonly Color C_EditBg  = new Color(0.08f, 0.18f, 0.32f, 1.00f);

        // ── Font ──────────────────────────────────────────────────────────────
        private static object _guiFont;
        private static bool   _fontChecked;

        internal static Font GetFont()
        {
            if (_fontChecked) return (Font)_guiFont; // null is fine — falls back to skin font
            _fontChecked = true;
            try { _guiFont = Font.CreateDynamicFontFromOSFont("Arial", 12); }
            catch { }
            return (Font)_guiFont;
        }

        // ── Typography quarantine ─────────────────────────────────────────────
        internal enum TextAlign { Default, MiddleLeft, MiddleCenter }

        private static bool _typographyBroken;

        internal static GUIStyle WithText(GUIStyle s, int fontSize,
                                          bool bold = false, TextAlign align = TextAlign.Default)
        {
            s.fontSize = fontSize; 
            if (_typographyBroken) return s;

            try { ApplyTypography(s, bold, align); }
            catch (Exception e)
            {
                _typographyBroken = true;
                MelonLogger.Warning(
                    "[FruitLib] UnityEngine.TextRenderingModule unavailable — menu and HUD fall back " +
                    "to the default font (no bold, no vertical centring). Usually means a corrupt " +
                    "MelonLoader/Il2CppAssemblies dump; deleting that folder and relaunching " +
                    "regenerates it. Cause: " + e.Message);
            }
            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyTypography(GUIStyle s, bool bold, TextAlign align)
        {
            s.font = GetFont();
            if (bold) s.fontStyle = FontStyle.Bold;
            if      (align == TextAlign.MiddleLeft)   s.alignment = TextAnchor.MiddleLeft;
            else if (align == TextAlign.MiddleCenter) s.alignment = TextAnchor.MiddleCenter;
        }

        // ── Style cache, invalidated when Scale changes ──────────────────────
        private static float _cachedScale = -1f;

        private static void CheckScale()
        {
            float s = Scale;
            if (Mathf.Abs(s - _cachedScale) < 0.01f) return;
            _cachedScale = s;
            _modTabStyle = _smallTabStyle = _catTabStyle = null;
            _fieldLabelStyle = _valueStyle = _valueClickStyle = null;
            _editStyle = _dimStyle = _toggleStyle = null;
            ModEntry._smallBtnStyle = null;
        }

        // ── Draw ──────────────────────────────────────────────────────────────
        internal static void Draw()
        {
            if (_drawBroken || _state == MenuState.Closed || _mods.Count == 0) return;

            var savedColor   = GUI.color;
            var savedContent = GUI.contentColor;
            var savedBg      = GUI.backgroundColor;
            try
            {
                CheckScale();
                GUI.color        = Color.white;
                GUI.contentColor = Color.white;
                GUI.backgroundColor = Color.white;

                if (_state == MenuState.Button)
                {
                    // Only when the pause menu would not take our button - see FruitMenuNative.
                    if (!FruitMenuNative.Present) DrawToggleButton();
                }
                else DrawPanel();
                _drawErrors = 0;
            }
            catch (Exception e)
            {
                // A throw here repeats on every OnGUI event: say it once, and give up only if it
                // keeps failing, so one bad frame doesn't take the menu away for the session.
                if (_drawErrors++ == 0)
                    MelonLogger.Error($"[FruitLib] Menu draw error: {e}");
                if (_drawErrors >= 120)
                {
                    _drawBroken = true;
                    MelonLogger.Error("[FruitLib] Menu panel disabled: it failed to draw 120 times in a row.");
                }
            }
            finally
            {
                GUI.color        = savedColor;
                GUI.contentColor = savedContent;
                GUI.backgroundColor = savedBg;
            }
        }

        private static bool _drawBroken;
        private static int  _drawErrors;

        /// <summary>
        /// The corner button, drawn only when the native MODS entry could not be built.
        ///
        /// Kept rather than deleted because it is the one route to mod settings that depends
        /// on nothing but IMGUI: if a future build moves the pause menu out from under
        /// FruitMenuNative, this is what keeps every mod's config reachable until it is
        /// ported.
        /// </summary>
        private static void DrawToggleButton()
        {
            float s = Scale;
            GUI.backgroundColor = C_TabN;
            if (GUI.Button(new Rect(20f * s, 20f * s, 130f * s, 28f * s),
                           "Mod Settings", SmallTabStyle()))
                OpenSettings();
            GUI.backgroundColor = Color.white;
        }

        private static void DrawPanel()
        {
            float ox = (Screen.width  - W) * 0.5f;
            float oy = (Screen.height - H) * 0.5f;

            int   catRows = (_selectedMod < _mods.Count) ? _mods[_selectedMod].CatRowCount : 1;
            float headerH = HeaderH(catRows);

            Rect(ox, oy, W, H,        C_Panel);
            Rect(ox, oy, W, headerH,  C_Header);

            // ── Mod tabs + Back ───────────────────────────────────────────────
            float modY  = oy + PAD;
            float backW = 64f * Scale;
            float tabsW = W - PAD * 2f - backW - 4f * Scale;
            float modTW = _mods.Count > 0 ? tabsW / _mods.Count : tabsW;

            for (int i = 0; i < _mods.Count; i++)
            {
                var r = new Rect(ox + PAD + i * modTW, modY, modTW - 2f * Scale, MOD_TH);
                GUI.backgroundColor = (i == _selectedMod) ? C_ModSel : C_TabN;
                if (GUI.Button(r, _mods[i].DisplayName, ModTabStyle()))
                    _selectedMod = i;
            }

            GUI.backgroundColor = C_TabN;
            bool back = GUI.Button(
                new Rect(ox + W - PAD - backW, modY, backW, MOD_TH),
                "← Back", ModTabStyle());
            GUI.backgroundColor = Color.white;
            if (back) { CloseSettings(); return; }

            // ── Category tabs ─────────────────────────────────────────────────
            if (_selectedMod < _mods.Count)
            {
                float catY = modY + MOD_TH + 4f * Scale;
                _mods[_selectedMod].DrawCategoryTabs(ox + PAD, catY, W - PAD * 2f, CAT_TH, CAT_GAP);
            }

            Rect(ox, oy + headerH - 2f * Scale, W, 2f * Scale, C_Divide);

            // ── Content area ──────────────────────────────────────────────────
            float footerH  = ROW + PAD * 2f;
            float contentY = oy + headerH;
            float contentH = H - headerH - footerH;
            Rect(ox, contentY, W, contentH, C_Content);

            if (_selectedMod < _mods.Count)
            {
                var contentRect = new Rect(
                    ox + PAD, contentY + 4f * Scale,
                    W - PAD * 2f, contentH - 8f * Scale);
                _mods[_selectedMod].DrawFields(contentRect);
            }

            // ── Footer ────────────────────────────────────────────────────────
            float footerY = oy + H - footerH;
            Rect(ox, footerY, W, footerH, C_Header);
            float resetW = 150f * Scale;
            GUI.backgroundColor = C_Minus;
            if (_selectedMod < _mods.Count &&
                GUI.Button(new Rect(ox + W - PAD - resetW, footerY + PAD, resetW, ROW),
                           "Reset to Defaults", ModTabStyle()))
                _mods[_selectedMod].ResetToDefaults();
            GUI.backgroundColor = Color.white;
        }

        // ── Primitives ────────────────────────────────────────────────────────
        internal static void Rect(float x, float y, float w, float h, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(new UnityEngine.Rect(x, y, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        // ── Styles ────────────────────────────────────────────────────────────
        private static GUIStyle _modTabStyle, _smallTabStyle, _catTabStyle,
                                _fieldLabelStyle, _valueStyle, _valueClickStyle,
                                _editStyle, _dimStyle, _toggleStyle;

        private static int FS(int baseSize) => Mathf.Max(8, Mathf.RoundToInt(baseSize * Scale));

        private static GUIStyle ModTabStyle() => _modTabStyle ??= WithText(new GUIStyle(GUI.skin.button)
        {
            normal  = { textColor = new Color(0.90f, 0.90f, 0.90f) },
            hover   = { textColor = Color.white },
            active  = { textColor = Color.white }
        }, FS(12), bold: true);

        private static GUIStyle SmallTabStyle() => _smallTabStyle ??= WithText(new GUIStyle(GUI.skin.button)
        {
            normal  = { textColor = new Color(0.88f, 0.88f, 0.88f) },
            hover   = { textColor = Color.white },
            active  = { textColor = Color.white }
        }, FS(11));

        private static GUIStyle CatTabStyle() => _catTabStyle ??= WithText(new GUIStyle(GUI.skin.button)
        {
            normal  = { textColor = new Color(0.88f, 0.88f, 0.88f) },
            hover   = { textColor = Color.white },
            active  = { textColor = Color.white }
        }, FS(11));

        private static GUIStyle FieldLabelStyle() => _fieldLabelStyle ??= WithText(new GUIStyle(GUI.skin.label)
        {
            normal    = { textColor = new Color(0.88f, 0.88f, 0.88f) }
        }, FS(11), align: TextAlign.MiddleLeft);

        private static GUIStyle ValueClickStyle() => _valueClickStyle ??= WithText(new GUIStyle(GUI.skin.button)
        {
            normal    = { textColor = Color.white },
            hover     = { textColor = new Color(0.85f, 0.92f, 1.00f) },
            active    = { textColor = Color.white }
        }, FS(11), align: TextAlign.MiddleCenter);

        private static GUIStyle EditStyle() => _editStyle ??= WithText(new GUIStyle(GUI.skin.label)
        {
            normal    = { textColor = new Color(0.95f, 0.95f, 0.60f) }
        }, FS(11), align: TextAlign.MiddleCenter);

        private static GUIStyle ValueStyle() => _valueStyle ??= WithText(new GUIStyle(GUI.skin.label)
        {
            normal    = { textColor = Color.white }
        }, FS(11), align: TextAlign.MiddleCenter);

        private static GUIStyle DimStyle() => _dimStyle ??= WithText(new GUIStyle(GUI.skin.label)
        {
            normal    = { textColor = new Color(0.72f, 0.72f, 0.72f) }
        }, FS(11), align: TextAlign.MiddleLeft);

        private static GUIStyle ToggleStyle() => _toggleStyle ??= WithText(new GUIStyle(GUI.skin.button)
        {
            normal  = { textColor = Color.white },
            hover   = { textColor = Color.white },
            active  = { textColor = Color.white }
        }, FS(11), bold: true);

        // ── Per-mod entry ─────────────────────────────────────────────────────
        private class ModEntry
        {
            public readonly string DisplayName;
            private readonly string _iniPath;            // FruitLib config class; null for MelonPreferences
            private readonly Action _saveElsewhere;      // MelonPreferences; null for a config class
            private readonly Action _ownerSave;          // the config class's own ini writer, if it gave one
            private readonly List<FruitSetting> _all = new List<FruitSetting>();

            private readonly List<string>                           _catOrder = new List<string>();
            private readonly Dictionary<string, List<FruitSetting>> _cats     = new Dictionary<string, List<FruitSetting>>();
            private string _selectedCat;
            private float  _scrollY, _maxScrollY;

            public  int   CatRowCount { get; private set; } = 1;
            private int   _catsPerRow;
            private float _catTabW;
            private float _lastTotalW = -1f;

            private static FruitSetting _editField;
            private static string       _editBuf    = "";
            private static int          _editCursor = 0;

            private static FruitSetting _rebindField;

            internal static void CancelEdit()
            {
                _editField   = null; _editBuf = ""; _editCursor = 0;
                _rebindField = null;
            }
            internal static GUIStyle _smallBtnStyle;

            /// <summary>A FruitLib mod: its config class's public static fields, saved to its ini.</summary>
            public ModEntry(string name, string iniPath, Type configType, Action save)
            {
                DisplayName = name;
                _iniPath    = iniPath;
                _ownerSave  = save;
                foreach (var f in configType.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var setting = FruitSetting.FromField(f);
                    if (setting != null) Add(setting);
                }
                _selectedCat = _catOrder.Count > 0 ? _catOrder[0] : "";
            }

            /// <summary>A MelonPreferences category, saved through MelonLoader.</summary>
            public ModEntry(string name, List<FruitSetting> settings, Action save)
            {
                DisplayName    = name;
                _saveElsewhere = save;
                foreach (var setting in settings) Add(setting);
                _selectedCat = _catOrder.Count > 0 ? _catOrder[0] : "";
            }

            private void Add(FruitSetting setting)
            {
                _all.Add(setting);
                if (setting.Category == null) return;   // no category = stored, never drawn
                if (!_cats.TryGetValue(setting.Category, out var list))
                {
                    _cats[setting.Category] = list = new List<FruitSetting>();
                    _catOrder.Add(setting.Category);
                }
                list.Add(setting);
            }

            /// <summary>
            /// Fills <paramref name="into"/> with the settings a native control can draw.
            ///
            /// The range comes from a MenuRange attribute (or a MelonPreferences ValueRange)
            /// when there is one. When not, it is derived from the default rather than the
            /// current value - deriving from the current value would make the range move as
            /// you drag, which is unusable. The guess is deliberately generous, because a
            /// slider that cannot reach a value is worse than one that is coarse.
            /// </summary>
            internal List<string> Categories() => new List<string>(_catOrder);

            internal void DescribeFields(List<FruitField> into, string only = null)
            {
                foreach (var cat in _catOrder)
                {
                    if (only != null && cat != only) continue;
                    if (!_cats.TryGetValue(cat, out var settings)) continue;

                    foreach (var f in settings)
                    {
                        if (f.Type == typeof(bool))
                        {
                            // A momentary bool is a button press, not a setting; a toggle
                            // would misrepresent it as state that sticks.
                            if (f.Momentary) continue;
                            into.Add(new FruitField { Setting = f, Label = f.Label, Kind = FruitFieldKind.Bool });
                            continue;
                        }

                        if (f.Type == typeof(KeyCode))
                        {
                            into.Add(new FruitField { Setting = f, Label = f.Label, Kind = FruitFieldKind.Key });
                            continue;
                        }

                        bool isInt = f.Type == typeof(int);
                        if (!isInt && f.Type != typeof(float)) continue;   // string, and the rest

                        float min, max;
                        if (f.HasRange) { min = f.Min; max = f.Max; }
                        else DeriveRange(f.Default ?? f.Value, isInt, out min, out max);

                        into.Add(new FruitField
                        {
                            Setting = f, Label = f.Label, Kind = FruitFieldKind.Number,
                            IsInt = isInt, Min = min, Max = max, RangeDeclared = f.HasRange,
                        });
                    }
                }
            }

            private static void DeriveRange(object def, bool isInt, out float min, out float max)
            {
                float d = def == null ? 0f : (isInt ? (int)def : (float)def);

                if (d > 0f)      { min = 0f;       max = d * 4f; }
                else if (d < 0f) { min = d * 2f;   max = -d * 2f; }
                else             { min = 0f;       max = isInt ? 10f : 1f; }
            }

            internal void Save() => WriteIni();

            public void ResetToDefaults()
            {
                foreach (var f in _all)
                    if (f.Default != null) f.Value = f.Default;
                WriteIni();
            }

            private void ComputeCatLayout(float totalW)
            {
                _lastTotalW = totalW;
                int   n    = _catOrder.Count;
                float minW = 72f * Scale;
                if (n <= 1)
                {
                    CatRowCount = n; _catsPerRow = n; _catTabW = totalW; return;
                }
                float w1 = totalW / n;
                if (w1 >= minW)
                {
                    CatRowCount = 1; _catsPerRow = n; _catTabW = w1;
                }
                else
                {
                    CatRowCount = 2; _catsPerRow = (n + 1) / 2; _catTabW = totalW / _catsPerRow;
                }
            }

            public void DrawCategoryTabs(float x, float y, float totalW, float rowH, float rowGap)
            {
                if (_catOrder.Count <= 1) return;
                if (Mathf.Abs(totalW - _lastTotalW) > 1f) ComputeCatLayout(totalW);

                for (int i = 0; i < _catOrder.Count; i++)
                {
                    string cat    = _catOrder[i];
                    int    rowIdx = i / _catsPerRow;
                    int    colIdx = i % _catsPerRow;
                    float  rx     = x + colIdx * _catTabW;
                    float  ry     = y + rowIdx * (rowH + rowGap);
                    var    r      = new UnityEngine.Rect(rx, ry, _catTabW - 2f * Scale, rowH);

                    GUI.backgroundColor = (cat == _selectedCat) ? C_CatSel : C_TabN;
                    if (GUI.Button(r, cat, CatTabStyle()))
                    {
                        CommitEdit();
                        _selectedCat = cat;
                        _scrollY = 0f;
                    }
                }
                GUI.backgroundColor = Color.white;
            }

            public void DrawFields(UnityEngine.Rect area)
            {
                if (_selectedCat == "" || !_cats.ContainsKey(_selectedCat)) return;
                var fields = _cats[_selectedCat];

                var ev = Event.current;

                if (_editField == null && ev.type == EventType.ScrollWheel
                    && area.Contains(ev.mousePosition))
                {
                    _scrollY = Mathf.Clamp(_scrollY + ev.delta.y * ROW * 1.2f, 0f, _maxScrollY);
                    ev.Use();
                }

                GUI.BeginGroup(area);
                bool dirty = false;
                int  row   = 0;

                // ── Column layout ─────────────────────────────────────────────
                float cw      = area.width;
                float labelX  = 6f * Scale;
                float labelW  = cw * 0.355f;
                float ctrlX   = cw * 0.370f;
                float gap     = 2f * Scale;
                float stepW   = cw * 0.046f;   // each of the 6 step buttons
                float numValW = cw * 0.160f;   // value display box
                float keyBtnW = cw * 0.200f;   // KeyCode rebind button
                float boolW   = cw * 0.100f;
                float kcW     = cw - ctrlX - gap; // fullwidth span for string labels

                float m3X  = ctrlX;                          // biggest minus (outer-left)
                float m2X  = m3X + stepW + gap;
                float m1X  = m2X + stepW + gap;              // smallest minus (inner-left)
                float valX = m1X + stepW + gap;              // value box
                float p1X  = valX + numValW + gap;           // smallest plus (inner-right)
                float p2X  = p1X + stepW + gap;
                float p3X  = p2X + stepW + gap;              // biggest plus (outer-right)

                foreach (var f in fields)
                {
                    float fy = row * (ROW + RGAP) - _scrollY;
                    row++;
                    if (fy + ROW < 0f || fy > area.height) continue;

                    if (row % 2 == 0)
                        FruitMenu.Rect(0f, fy, cw, ROW, C_RowAlt);

                    object current = f.Value;

                    // Field name — vertically centred
                    GUI.Label(new UnityEngine.Rect(labelX, fy, labelW, ROW),
                              f.Label, FieldLabelStyle());

                    // ── bool ─────────────────────────────────────────────────
                    if (f.Type == typeof(bool))
                    {
                        if (f.Momentary)
                        {
                            GUI.backgroundColor = C_Action;
                            if (GUI.Button(new UnityEngine.Rect(ctrlX, fy, boolW, ROW),
                                           "", ToggleStyle()))
                                f.Value = true;
                            GUI.backgroundColor = Color.white;
                        }
                        else
                        {
                            bool val = (bool)current;
                            GUI.backgroundColor = val ? C_BoolOn : C_BoolOff;
                            bool next = GUI.Toggle(new UnityEngine.Rect(ctrlX, fy, boolW, ROW),
                                                   val, val ? "ON" : "OFF", ToggleStyle());
                            GUI.backgroundColor = Color.white;
                            if (next != val) { f.Value = next; dirty = true; }
                        }
                    }
                    // ── KeyCode — clickable, enters rebind mode ───────────────
                    else if (f.Type == typeof(KeyCode))
                    {
                        bool rebinding = (_rebindField == f);
                        if (rebinding)
                        {
                            HandleRebindKey(f, ref dirty);
                            FruitMenu.Rect(ctrlX, fy, keyBtnW, ROW, C_EditBg);
                            GUI.Label(new UnityEngine.Rect(ctrlX, fy, keyBtnW, ROW),
                                      "[ press key… ]", EditStyle());
                        }
                        else
                        {
                            string keyName = current?.ToString() ?? "None";
                            GUI.backgroundColor = C_TabN;
                            if (GUI.Button(new UnityEngine.Rect(ctrlX, fy, keyBtnW, ROW),
                                           keyName, ValueClickStyle()))
                            {
                                ClearEdit();
                                _rebindField = f;
                            }
                            GUI.backgroundColor = Color.white;
                        }
                    }
                    // ── string — read-only display ────────────────────────────
                    else if (f.Type == typeof(string))
                    {
                        GUI.Label(new UnityEngine.Rect(ctrlX, fy, kcW, ROW),
                                  current?.ToString() ?? "", DimStyle());
                    }
                    // ── float ────────────────────────────────────────────────
                    else if (f.Type == typeof(float))
                    {
                        float val     = (float)current;
                        float base0   = FloatStep(val);
                        float s0 = base0 * 100f;
                        float s1 = base0 * 10f;
                        float s2 = base0;
                        bool  editing = (_editField == f);

                        // Minus buttons (outer -> inner = big -> small)
                        GUI.backgroundColor = C_Minus;
                        if (GUI.Button(new UnityEngine.Rect(m3X, fy, stepW, ROW), StepLabel(s0), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val - s0; dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(m2X, fy, stepW, ROW), StepLabel(s1), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val - s1; dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(m1X, fy, stepW, ROW), StepLabel(s2), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val - s2; dirty = true; }
                        GUI.backgroundColor = Color.white;

                        // custom value box, click to enter edit mode
                        if (editing)
                        {
                            HandleEditKeys(f, ref dirty);
                            FruitMenu.Rect(valX, fy, numValW, ROW, C_EditBg);
                            string disp = _editBuf.Substring(0, _editCursor) + "|"
                                        + _editBuf.Substring(_editCursor);
                            GUI.Label(new UnityEngine.Rect(valX, fy, numValW, ROW), disp, EditStyle());
                        }
                        else
                        {
                            string display = val.ToString("0.###", CultureInfo.InvariantCulture);
                            GUI.backgroundColor = C_TabN;
                            if (GUI.Button(new UnityEngine.Rect(valX, fy, numValW, ROW),
                                           display, ValueClickStyle()))
                            { CommitEdit(); _editField = f; _editBuf = display; _editCursor = display.Length; }
                            GUI.backgroundColor = Color.white;
                        }

                        // Plus buttons (inner -> outer = small -> big)
                        GUI.backgroundColor = C_Plus;
                        if (GUI.Button(new UnityEngine.Rect(p1X, fy, stepW, ROW), StepLabel(s2), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val + s2; dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(p2X, fy, stepW, ROW), StepLabel(s1), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val + s1; dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(p3X, fy, stepW, ROW), StepLabel(s0), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val + s0; dirty = true; }
                        GUI.backgroundColor = Color.white;
                    }
                    // ── int ──────────────────────────────────────────────────
                    else if (f.Type == typeof(int))
                    {
                        int  val     = (int)current;
                        int  base0   = IntStep(val);
                        int  s0      = base0 * 10;
                        int  s1      = base0;
                        int  s2      = Math.Max(1, base0 / 10);
                        bool editing = (_editField == f);

                        // Minus buttons
                        GUI.backgroundColor = C_Minus;
                        if (GUI.Button(new UnityEngine.Rect(m3X, fy, stepW, ROW), StepLabel(s0), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val - s0; dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(m2X, fy, stepW, ROW), StepLabel(s1), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val - s1; dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(m1X, fy, stepW, ROW), StepLabel(s2), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val - s2; dirty = true; }
                        GUI.backgroundColor = Color.white;

                        // Value box
                        if (editing)
                        {
                            HandleEditKeys(f, ref dirty);
                            FruitMenu.Rect(valX, fy, numValW, ROW, C_EditBg);
                            string disp = _editBuf.Substring(0, _editCursor) + "|"
                                        + _editBuf.Substring(_editCursor);
                            GUI.Label(new UnityEngine.Rect(valX, fy, numValW, ROW), disp, EditStyle());
                        }
                        else
                        {
                            string display = val.ToString();
                            GUI.backgroundColor = C_TabN;
                            if (GUI.Button(new UnityEngine.Rect(valX, fy, numValW, ROW),
                                           display, ValueClickStyle()))
                            { CommitEdit(); _editField = f; _editBuf = display; _editCursor = display.Length; }
                            GUI.backgroundColor = Color.white;
                        }

                        // Plus buttons
                        GUI.backgroundColor = C_Plus;
                        if (GUI.Button(new UnityEngine.Rect(p1X, fy, stepW, ROW), StepLabel(s2), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val + s2; dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(p2X, fy, stepW, ROW), StepLabel(s1), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val + s1; dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(p3X, fy, stepW, ROW), StepLabel(s0), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.Value = val + s0; dirty = true; }
                        GUI.backgroundColor = Color.white;
                    }
                }

                _maxScrollY = Mathf.Max(0f, row * (ROW + RGAP) - area.height);
                GUI.EndGroup();

                if (dirty) WriteIni();
            }

            private static void HandleEditKeys(FruitSetting f, ref bool dirty)
            {
                var ev = Event.current;
                if (ev.type != EventType.KeyDown) return;

                bool isFloat = f.Type == typeof(float);

                switch (ev.keyCode)
                {
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                        TryApplyBuffer(f, ref dirty);
                        ClearEdit();
                        ev.Use(); break;

                    case KeyCode.Escape:
                        ClearEdit();
                        ev.Use(); break;

                    case KeyCode.LeftArrow:
                        if (_editCursor > 0) _editCursor--;
                        ev.Use(); break;

                    case KeyCode.RightArrow:
                        if (_editCursor < _editBuf.Length) _editCursor++;
                        ev.Use(); break;

                    case KeyCode.Home:
                        _editCursor = 0;
                        ev.Use(); break;

                    case KeyCode.End:
                        _editCursor = _editBuf.Length;
                        ev.Use(); break;

                    case KeyCode.Backspace:
                        if (_editCursor > 0)
                        {
                            _editBuf = _editBuf.Substring(0, _editCursor - 1)
                                     + _editBuf.Substring(_editCursor);
                            _editCursor--;
                            TryApplyBuffer(f, ref dirty);
                        }
                        ev.Use(); break;

                    case KeyCode.Delete:
                        if (_editCursor < _editBuf.Length)
                        {
                            _editBuf = _editBuf.Substring(0, _editCursor)
                                     + _editBuf.Substring(_editCursor + 1);
                            TryApplyBuffer(f, ref dirty);
                        }
                        ev.Use(); break;

                    default:
                        char ch = ev.character;
                        if (ch == '\0') break;
                        bool ok = char.IsDigit(ch)
                               || (ch == '-' && _editCursor == 0 && !_editBuf.StartsWith("-"))
                               || (ch == '.' && isFloat && !_editBuf.Contains('.'));
                        if (ok)
                        {
                            _editBuf = _editBuf.Substring(0, _editCursor)
                                     + ch
                                     + _editBuf.Substring(_editCursor);
                            _editCursor++;
                            TryApplyBuffer(f, ref dirty);
                        }
                        ev.Use(); break;
                }
            }

            private static void HandleRebindKey(FruitSetting f, ref bool dirty)
            {
                var ev = Event.current;
                if (ev.type == EventType.MouseDown)
                {
                    KeyCode[] mouseKeys = { KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2,
                                            KeyCode.Mouse3, KeyCode.Mouse4 };
                    if (ev.button >= 0 && ev.button < mouseKeys.Length)
                    {
                        f.Value = mouseKeys[ev.button];
                        _rebindField = null; dirty = true;
                        ev.Use();
                    }
                    return;
                }

                if (ev.type != EventType.KeyDown) return;

                switch (ev.keyCode)
                {
                    case KeyCode.Escape:
                        _rebindField = null;
                        ev.Use(); break;

                    case KeyCode.Backspace:
                    case KeyCode.Delete:
                        f.Value = KeyCode.None;
                        _rebindField = null; dirty = true;
                        ev.Use(); break;

                    default:
                        if (ev.keyCode != KeyCode.None)
                        {
                            f.Value = ev.keyCode;
                            _rebindField = null; dirty = true;
                        }
                        ev.Use(); break;
                }
            }

            private static void TryApplyBuffer(FruitSetting f, ref bool dirty)
            {
                if (f.Type == typeof(float))
                {
                    if (float.TryParse(_editBuf, NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out float fv))
                    { f.Value = fv; dirty = true; }
                }
                else if (f.Type == typeof(int))
                {
                    if (int.TryParse(_editBuf, out int iv))
                    { f.Value = iv; dirty = true; }
                }
            }

            private static void ClearEdit() { _editField = null; _editBuf = ""; _editCursor = 0; _rebindField = null; }

            private static bool CommitEdit()
            {
                _rebindField = null;
                if (_editField == null) return false;
                bool dummy = false;
                TryApplyBuffer(_editField, ref dummy);
                ClearEdit();
                return dummy;
            }

            // ── Helpers ───────────────────────────────────────────────────────
            private static GUIStyle SmallBtnStyle() => _smallBtnStyle ??= WithText(new GUIStyle(GUI.skin.button)
            {
                normal  = { textColor = Color.white },
                hover   = { textColor = Color.white },
                active  = { textColor = Color.white }
            }, FS(8), bold: true);
            private static string StepLabel(float s)
            {
                if (s >= 1f)   return s.ToString("0", CultureInfo.InvariantCulture);
                if (s >= 0.1f) return ".1";
                if (s >= 0.01f) return ".01";
                return s.ToString("0.###", CultureInfo.InvariantCulture);
            }

            private static string StepLabel(int s) => s.ToString();

            private static float FloatStep(float v)
            {
                float a = Mathf.Abs(v);
                if (a >= 1000f) return 10f;
                if (a >= 100f)  return 1f;
                if (a >= 10f)   return 0.1f;
                if (a >= 1f)    return 0.01f;
                return 0.001f;
            }

            private static int IntStep(int v)
            {
                int a = Math.Abs(v);
                if (a >= 10000) return 1000;
                if (a >= 1000)  return 100;
                if (a >= 100)   return 10;
                return 1;
            }


            private void WriteIni()
            {
                try
                {
                    if (_saveElsewhere != null) { _saveElsewhere(); return; }

                    if (_ownerSave != null)
                    {
                        _ownerSave();
                        FruitMenu.OnConfigChanged?.Invoke();
                        return;
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("# Written by FruitLib in-game menu — comments restored on restart.");
                    foreach (var f in _all)
                    {
                        if (f.Momentary) continue;
                        object val = f.Value;
                        string s = f.Type == typeof(float)
                            ? ((float)val).ToString("0.##############", CultureInfo.InvariantCulture)
                            : val?.ToString() ?? "";
                        sb.AppendLine($"{f.Key} = {s}");
                    }
                    File.WriteAllText(_iniPath, sb.ToString());
                    FruitMenu.OnConfigChanged?.Invoke();
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[FruitLib] Save failed for {DisplayName}: {e.Message}");
                }
            }
        }
    }
}
