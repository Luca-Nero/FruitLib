using System;
using System.Collections.Generic;
using Il2CppTMPro;
using Il2CppViews.Game;
using Il2CppViews.Toolbar;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// The HUD drawn with the game's own UI: a screen-space canvas of TextMeshPro rows in the
    /// font, material and letter spacing of the game's HUD text.
    ///
    /// The font is borrowed at runtime rather than shipped: it is whatever the game's kill
    /// counter uses, else the toolbar item hint, else any live TextMeshPro text. Until one of
    /// those exists (the main menu has none of them), <see cref="Active"/> is false and
    /// FruitHud keeps drawing with IMGUI; the switch-over is one frame.
    ///
    /// The canvas scales against 1080p, so the HUD settings (margins, text size) mean the same
    /// at any resolution. Text is only re-set when it changes: a TextMeshPro text change
    /// rebuilds its mesh, and a panel that redraws every frame should not rebuild every frame.
    /// </summary>
    internal static class FruitHudNative
    {
        private const float ReferenceHeight = 1080f;
        private const int   SortingOrder    = 1000;

        private static GameObject      _root;
        private static RectTransform   _rootRt;
        private static Canvas          _canvas;
        private static TextMeshProUGUI _measure;

        private static TMP_FontAsset _font;
        private static Material      _fontMaterial;
        private static float         _spacing;
        private static string        _fontSource;
        private static int           _fontRank;   // 0 none, 1 any text, 2 toolbar hint, 3 kill counter

        private static bool _failed;
        private static int  _pollCountdown;
        private static bool _wasActive;

        private static readonly Dictionary<HudHandle, View>    _byHandle = new Dictionary<HudHandle, View>();
        private static readonly List<FruitHud.Placement>       _placed = new List<FruitHud.Placement>();
        private static readonly Color C_Rule = new Color(0.45f, 0.45f, 0.45f, 0.8f);

        /// <summary>True while the native HUD is the one drawing; IMGUI stays out of the way.</summary>
        internal static bool Active =>
            !_failed && FruitLibConfig.NativeHud && _font != null && _root != null;

        // ── Per frame ────────────────────────────────────────────────────────────────

        internal static void Tick()
        {
            bool active = Active;
            if (active != _wasActive)
            {
                // IMGUI and TextMeshPro set the same text at different widths.
                FruitHud.ForgetMeasurements();
                _wasActive = active;
            }

            if (_failed) return;

            if (!FruitLibConfig.NativeHud)
            {
                if (_canvas != null) _canvas.enabled = false;
                return;
            }

            // Keep looking until the font is the HUD's own: the main menu only offers stand-ins.
            if (_font == null) _fontRank = 0;
            if (_fontRank < 2) FindFont();
            if (_font == null) return;
            if (_root == null) Build();

            bool show = FruitHud.ShouldDraw;
            _canvas.enabled = show;
            if (!show) return;

            var size = _rootRt.rect.size;
            FruitHud.Layout(size.x, size.y, _placed, Measure, LineHeight);

            foreach (var v in _byHandle.Values) v.Used = false;
            foreach (var p in _placed) Apply(p);
            foreach (var v in _byHandle.Values) if (!v.Used && v.Root.activeSelf) v.Root.SetActive(false);
        }

        internal static void Fail(string step, Exception e)
        {
            if (_failed) return;
            _failed = true;
            MelonLogger.Warning($"[FruitHud] the native HUD failed ({step}); drawing with IMGUI for the rest of the session: {e}");
            try { if (_root != null) Object.Destroy(_root); } catch { }
            _root = null;
            _byHandle.Clear();
        }

        // ── The font ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Looks once a second for a better font than the one in use. The HUD's own text only
        /// exists in a level; anything found before that is a stand-in, replaced as soon as the
        /// kill counter or the toolbar hint shows up.
        /// </summary>
        private static void FindFont()
        {
            if (--_pollCountdown > 0) return;
            _pollCountdown = 60;

            TextMeshProUGUI source = null;
            int rank = 0;
            string from = null;

            try { source = Usable(FruitScene.First<KillsLine>()?.m_text); if (source != null) { rank = 3; from = "kill counter"; } } catch { }
            if (source == null)
                try { source = Usable(FruitScene.First<ToolbarItemHintView>()?.m_text); if (source != null) { rank = 2; from = "toolbar hint"; } } catch { }
            if (source == null && _fontRank == 0)
            {
                try
                {
                    foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>(false))
                    {
                        if (Usable(t) == null || t.name.StartsWith("FruitLib")) continue;
                        source = t; rank = 1; from = $"'{t.name}' (stand-in until a level loads)";
                        break;
                    }
                }
                catch { }
            }
            if (source == null || rank <= _fontRank) return;

            _font         = source.font;
            _fontMaterial = source.fontSharedMaterial;
            _spacing      = source.characterSpacing;
            _fontRank     = rank;
            _fontSource   = from;
            FruitLog.Info($"[FruitHud] native HUD using {_font.name} from the {_fontSource}.");

            // A new font invalidates every row built with the old one, and every measured width.
            if (_root != null) { Object.Destroy(_root); _root = null; _byHandle.Clear(); }
            FruitHud.ForgetMeasurements();
        }

        private static TextMeshProUGUI Usable(TextMeshProUGUI t) => t != null && t.font != null ? t : null;

        // ── Building ─────────────────────────────────────────────────────────────────

        private static void Build()
        {
            _byHandle.Clear();

            _root = new GameObject("FruitLib_HUD");
            Object.DontDestroyOnLoad(_root);

            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = SortingOrder;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceHeight * 16f / 9f, ReferenceHeight);
            scaler.matchWidthOrHeight  = 1f;   // by height, so ultrawide gets room, not smaller text

            _rootRt = _root.GetComponent<RectTransform>();

            // Never shown: only asked how wide a string would be.
            _measure = NewText(_rootRt, "FruitLib_Measure");
            _measure.color = new Color(0f, 0f, 0f, 0f);

            FruitLog.Info("[FruitHud] native HUD canvas built.");
        }

        private static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);   // top-left, y down as negative
            return rt;
        }

        private static TextMeshProUGUI NewText(Transform parent, string name)
        {
            var t = NewRect(parent, name).gameObject.AddComponent<TextMeshProUGUI>();
            t.font               = _font;
            if (_fontMaterial != null) t.fontSharedMaterial = _fontMaterial;
            t.characterSpacing   = _spacing;
            t.richText           = false;   // a '<' in a mod's text is text, not a tag
            t.raycastTarget      = false;
            t.textWrappingMode   = TextWrappingModes.NoWrap;
            t.overflowMode       = TextOverflowModes.Overflow;
            t.alignment          = TextAlignmentOptions.MidlineLeft;
            return t;
        }

        private static Image NewImage(Transform parent, string name)
        {
            var img = NewRect(parent, name).gameObject.AddComponent<Image>();
            img.raycastTarget = false;
            return img;
        }

        // ── Measuring ────────────────────────────────────────────────────────────────

        private static float Measure(string text, int fontSize, bool bold)
        {
            _measure.fontSize  = fontSize;
            _measure.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            return Mathf.Ceil(_measure.GetPreferredValues(text).x);
        }

        private static float LineHeight(int fontSize) => Mathf.Ceil(fontSize * 1.45f);

        // ── One panel ────────────────────────────────────────────────────────────────

        private sealed class View
        {
            public GameObject    Root;
            public RectTransform Rt;
            public Image         Bg;
            public readonly List<TextMeshProUGUI> Texts = new List<TextMeshProUGUI>();
            public readonly List<string>          Shown = new List<string>();
            public readonly List<Image>           Rules = new List<Image>();
            public bool Used;
        }

        /// <summary>
        /// Drops a handle's view when its panel is replaced or unregistered. Without this the
        /// canvas objects outlive the panel, switched off every frame, and the dictionary keeps
        /// the handle - and with it the mod's build callback - alive for the session.
        /// </summary>
        internal static void Forget(HudHandle h)
        {
            if (h == null || !_byHandle.TryGetValue(h, out var v)) return;
            _byHandle.Remove(h);
            try { if (v.Root != null) Object.Destroy(v.Root); } catch { }
        }

        private static View ViewFor(HudHandle h)
        {
            if (_byHandle.TryGetValue(h, out var v) && v.Root != null) return v;

            var rt = NewRect(_rootRt, "FruitLib_Panel_" + h.Name);
            v = new View { Root = rt.gameObject, Rt = rt, Bg = rt.gameObject.AddComponent<Image>() };
            v.Bg.raycastTarget = false;
            _byHandle[h] = v;
            return v;
        }

        private static void Apply(FruitHud.Placement p)
        {
            var v = ViewFor(p.Handle);
            v.Used = true;
            if (!v.Root.activeSelf) v.Root.SetActive(true);

            v.Rt.anchoredPosition = new Vector2(p.X, -p.Y);
            v.Rt.sizeDelta        = new Vector2(p.BoxW, p.BoxH);
            v.Bg.color            = new Color(0f, 0f, 0f, FruitLibConfig.BgAlpha);

            var rows = p.Handle.Panel.Rows;
            int texts = 0, rules = 0;

            for (int r = 0; r < rows.Count; r++)
            {
                var   row = rows[r];
                float top = FruitHud.PadY + r * p.LineH;

                if (row.Rule)
                {
                    if (rules == v.Rules.Count) { var img = NewImage(v.Rt, "Rule"); img.color = C_Rule; v.Rules.Add(img); }
                    var rule = v.Rules[rules++];
                    if (!rule.gameObject.activeSelf) rule.gameObject.SetActive(true);
                    rule.rectTransform.anchoredPosition = new Vector2(FruitHud.PadX, -(top + p.LineH * 0.5f));
                    rule.rectTransform.sizeDelta        = new Vector2(p.TextW, 1f);
                    continue;
                }

                if (string.IsNullOrEmpty(row.Text)) continue;   // a blank line: its height is the gap

                if (texts == v.Texts.Count) { v.Texts.Add(NewText(v.Rt, "Row")); v.Shown.Add(null); }
                var t = v.Texts[texts];
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);

                if (v.Shown[texts] != row.Text) { t.text = row.Text; v.Shown[texts] = row.Text; }
                if (t.fontSize != p.FontSize) t.fontSize = p.FontSize;
                var style = row.Bold ? FontStyles.Bold : FontStyles.Normal;
                if (t.fontStyle != style) t.fontStyle = style;
                if (t.color != row.Color) t.color = row.Color;

                t.rectTransform.anchoredPosition = new Vector2(FruitHud.PadX, -top);
                t.rectTransform.sizeDelta        = new Vector2(p.TextW, p.LineH);
                texts++;
            }

            for (int i = texts; i < v.Texts.Count; i++) if (v.Texts[i].gameObject.activeSelf) v.Texts[i].gameObject.SetActive(false);
            for (int i = rules; i < v.Rules.Count; i++) if (v.Rules[i].gameObject.activeSelf) v.Rules[i].gameObject.SetActive(false);
        }
    }
}
