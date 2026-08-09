using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Il2CppViews.Pause;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FruitLib.FruitLibMod), FruitLib.FruitVersion.MelonName,
                     FruitLib.FruitVersion.VersionConst, FruitLib.FruitVersion.MelonAuthor)]
[assembly: MelonGame]

namespace FruitLib
{
    [AttributeUsage(AttributeTargets.Field)]
    public class MenuCategoryAttribute : Attribute
    {
        public readonly string Name;
        public MenuCategoryAttribute(string name) => Name = name;
    }

    public enum ButtonKind { Toggle, Momentary }

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
        public static bool IsInputSuppressed  => IsOpen || JustClosed;

        private static readonly List<GameObject> _hidden = new List<GameObject>();
        internal static PauseViewController PauseVC;

        // ── Mod registry ──────────────────────────────────────────────────────
        private static readonly List<ModEntry> _mods = new List<ModEntry>();
        private static int _selectedMod;

        public static void Register(string displayName, string iniFilePath, Type configType)
        {
            _mods.Add(new ModEntry(displayName, iniFilePath, configType));
            MelonLogger.Msg($"[FruitLib] Registered: {displayName}");
        }

        // ── State transitions ─────────────────────────────────────────────────
        internal static void OnPauseAlphaChanged(bool isPaused)
        {
            if (isPaused  && _state == MenuState.Closed) _state = MenuState.Button;
            if (!isPaused && _state == MenuState.Button)  _state = MenuState.Closed;
        }

        internal static void OnGameResumed()
        {
            ModEntry.CancelEdit();
            RestoreNativeUI();
            _state = MenuState.Closed;
        }

        private static void OpenSettings()
        {
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
            if (_state == MenuState.Closed || _mods.Count == 0) return;
            CheckScale();
            var savedColor   = GUI.color;
            var savedContent = GUI.contentColor;
            var savedBg      = GUI.backgroundColor;
            GUI.color        = Color.white;
            GUI.contentColor = Color.white;
            GUI.backgroundColor = Color.white;

            if (_state == MenuState.Button) DrawToggleButton();
            else DrawPanel();

            GUI.color        = savedColor;
            GUI.contentColor = savedContent;
            GUI.backgroundColor = savedBg;
        }

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
            private readonly string _iniPath;
            private readonly FieldInfo[] _allFields;

            private readonly List<string>                        _catOrder = new List<string>();
            private readonly Dictionary<string, List<FieldInfo>> _cats     = new Dictionary<string, List<FieldInfo>>();
            private string _selectedCat;
            private float  _scrollY, _maxScrollY;

            public  int   CatRowCount { get; private set; } = 1;
            private int   _catsPerRow;
            private float _catTabW;
            private float _lastTotalW = -1f;

            private static FieldInfo _editField;
            private static string    _editBuf    = "";
            private static int       _editCursor = 0;

            private static FieldInfo _rebindField;

            internal static void CancelEdit()
            {
                _editField   = null; _editBuf = ""; _editCursor = 0;
                _rebindField = null;
            }
            internal static GUIStyle _smallBtnStyle;

            private readonly Dictionary<FieldInfo, object> _defaults = new Dictionary<FieldInfo, object>();

            public ModEntry(string name, string iniPath, Type configType)
            {
                DisplayName = name;
                _iniPath    = iniPath;
                _allFields  = configType.GetFields(BindingFlags.Public | BindingFlags.Static);

                foreach (var f in _allFields)
                {
                    if (f.IsSpecialName || !IsRenderable(f.FieldType)) continue;
                    _defaults[f] = f.GetValue(null);
                    var attr = (MenuCategoryAttribute)Attribute.GetCustomAttribute(
                                   f, typeof(MenuCategoryAttribute));
                    string cat = attr?.Name ?? "General";
                    if (!_cats.ContainsKey(cat))
                    {
                        _cats[cat] = new List<FieldInfo>();
                        _catOrder.Add(cat);
                    }
                    _cats[cat].Add(f);
                }

                _selectedCat = _catOrder.Count > 0 ? _catOrder[0] : "";
            }

            public void ResetToDefaults()
            {
                foreach (var kvp in _defaults)
                    kvp.Key.SetValue(null, kvp.Value);
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

                    object current = f.GetValue(null);

                    // Field name — vertically centred
                    GUI.Label(new UnityEngine.Rect(labelX, fy, labelW, ROW),
                              f.Name, FieldLabelStyle());

                    // ── bool ─────────────────────────────────────────────────
                    if (f.FieldType == typeof(bool))
                    {
                        var btnAttr = (MenuButtonAttribute)Attribute.GetCustomAttribute(
                                          f, typeof(MenuButtonAttribute));
                        if (btnAttr?.Kind == ButtonKind.Momentary)
                        {
                            GUI.backgroundColor = C_Action;
                            if (GUI.Button(new UnityEngine.Rect(ctrlX, fy, boolW, ROW),
                                           "", ToggleStyle()))
                                f.SetValue(null, true);
                            GUI.backgroundColor = Color.white;
                        }
                        else
                        {
                            bool val = (bool)current;
                            GUI.backgroundColor = val ? C_BoolOn : C_BoolOff;
                            bool next = GUI.Toggle(new UnityEngine.Rect(ctrlX, fy, boolW, ROW),
                                                   val, val ? "ON" : "OFF", ToggleStyle());
                            GUI.backgroundColor = Color.white;
                            if (next != val) { f.SetValue(null, next); dirty = true; }
                        }
                    }
                    // ── KeyCode — clickable, enters rebind mode ───────────────
                    else if (f.FieldType == typeof(KeyCode))
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
                    else if (f.FieldType == typeof(string))
                    {
                        GUI.Label(new UnityEngine.Rect(ctrlX, fy, kcW, ROW),
                                  current?.ToString() ?? "", DimStyle());
                    }
                    // ── float ────────────────────────────────────────────────
                    else if (f.FieldType == typeof(float))
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
                        { if (editing) ClearEdit(); f.SetValue(null, val - s0); dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(m2X, fy, stepW, ROW), StepLabel(s1), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.SetValue(null, val - s1); dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(m1X, fy, stepW, ROW), StepLabel(s2), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.SetValue(null, val - s2); dirty = true; }
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
                        { if (editing) ClearEdit(); f.SetValue(null, val + s2); dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(p2X, fy, stepW, ROW), StepLabel(s1), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.SetValue(null, val + s1); dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(p3X, fy, stepW, ROW), StepLabel(s0), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.SetValue(null, val + s0); dirty = true; }
                        GUI.backgroundColor = Color.white;
                    }
                    // ── int ──────────────────────────────────────────────────
                    else if (f.FieldType == typeof(int))
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
                        { if (editing) ClearEdit(); f.SetValue(null, val - s0); dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(m2X, fy, stepW, ROW), StepLabel(s1), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.SetValue(null, val - s1); dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(m1X, fy, stepW, ROW), StepLabel(s2), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.SetValue(null, val - s2); dirty = true; }
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
                        { if (editing) ClearEdit(); f.SetValue(null, val + s2); dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(p2X, fy, stepW, ROW), StepLabel(s1), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.SetValue(null, val + s1); dirty = true; }
                        if (GUI.Button(new UnityEngine.Rect(p3X, fy, stepW, ROW), StepLabel(s0), SmallBtnStyle()))
                        { if (editing) ClearEdit(); f.SetValue(null, val + s0); dirty = true; }
                        GUI.backgroundColor = Color.white;
                    }
                }

                _maxScrollY = Mathf.Max(0f, row * (ROW + RGAP) - area.height);
                GUI.EndGroup();

                if (dirty) WriteIni();
            }

            private static void HandleEditKeys(FieldInfo f, ref bool dirty)
            {
                var ev = Event.current;
                if (ev.type != EventType.KeyDown) return;

                bool isFloat = f.FieldType == typeof(float);

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

            private static void HandleRebindKey(FieldInfo f, ref bool dirty)
            {
                var ev = Event.current;
                if (ev.type == EventType.MouseDown)
                {
                    KeyCode[] mouseKeys = { KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2,
                                            KeyCode.Mouse3, KeyCode.Mouse4 };
                    if (ev.button >= 0 && ev.button < mouseKeys.Length)
                    {
                        f.SetValue(null, mouseKeys[ev.button]);
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
                        f.SetValue(null, KeyCode.None);
                        _rebindField = null; dirty = true;
                        ev.Use(); break;

                    default:
                        if (ev.keyCode != KeyCode.None)
                        {
                            f.SetValue(null, ev.keyCode);
                            _rebindField = null; dirty = true;
                        }
                        ev.Use(); break;
                }
            }

            private static void TryApplyBuffer(FieldInfo f, ref bool dirty)
            {
                if (f.FieldType == typeof(float))
                {
                    if (float.TryParse(_editBuf, NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out float fv))
                    { f.SetValue(null, fv); dirty = true; }
                }
                else if (f.FieldType == typeof(int))
                {
                    if (int.TryParse(_editBuf, out int iv))
                    { f.SetValue(null, iv); dirty = true; }
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

            private static bool IsRenderable(Type t) =>
                t == typeof(bool) || t == typeof(float) || t == typeof(int) ||
                t == typeof(string) || t == typeof(KeyCode);

            private void WriteIni()
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("# Written by FruitLib in-game menu — comments restored on restart.");
                    foreach (var f in _allFields)
                    {
                        if (f.IsSpecialName || !IsRenderable(f.FieldType)) continue;
                        var btnAttr = (MenuButtonAttribute)Attribute.GetCustomAttribute(
                                          f, typeof(MenuButtonAttribute));
                        if (btnAttr?.Kind == ButtonKind.Momentary) continue;
                        object val = f.GetValue(null);
                        string s = f.FieldType == typeof(float)
                            ? ((float)val).ToString("0.##############", CultureInfo.InvariantCulture)
                            : val?.ToString() ?? "";
                        sb.AppendLine($"{f.Name} = {s}");
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

    // ── MelonMod entry point ──────────────────────────────────────────────────
    public class FruitLibMod : MelonMod
    {
        private PauseViewController _pauseVC;
        private int _pollCountdown;
        private bool _wasMenuOpen;

        public override void OnInitializeMelon()
        {
            HarmonyInstance.PatchAll();

            FruitHud.Init();
            FruitPerfMon.RegisterPanel();
            FruitUpdateCheck.RegisterPanel();
            FruitUpdateCheck.Register("FruitLib", FruitVersion.Current, "Luca-Nero", "FruitLib");

            LoggerInstance.Msg($"FruitLib v{FruitVersion.Current} ready.");
        }

        public override void OnUpdate()
        {
            FruitPerfMon.Tick();
            FruitHud.Tick();
            FruitToolbar.Tick();

            if (_pauseVC == null)
            {
                if (--_pollCountdown > 0) return;
                _pollCountdown = 60;
                _pauseVC = UnityEngine.Object.FindObjectOfType<PauseViewController>(true);
                FruitMenu.PauseVC = _pauseVC;
                return;
            }

            var group = _pauseVC.m_group;
            FruitMenu.OnPauseAlphaChanged(group != null && group.alpha > 0.5f);

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
            FruitToolbar.ResetForScene();
        }

        [HarmonyPatch(typeof(PauseViewController), nameof(PauseViewController.dtz))]
        static class Patch_PauseResume
        {
            static void Prefix() => FruitMenu.OnGameResumed();
        }
    }
}
