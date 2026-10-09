using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    public enum HudCorner { TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3 }

    /// <summary>
    /// On-screen readouts from every mod, stacked into corners so they never overlap.
    ///
    /// Drawn with the game's own UI: a TextMeshPro canvas in the font the game's HUD uses,
    /// so a mod's panel reads as part of the game. Until that font has been found, or if
    /// the native build fails or the player turns it off (FruitLib settings: HUD > Native
    /// HUD), the same panels are drawn with IMGUI instead. Mods don't see the difference:
    /// both draw from one layout pass over the same <see cref="HudPanel"/> content.
    /// </summary>
    public static class FruitHud
    {
        public static HudHandle Register(string name, Action<HudPanel> build,
                                         int order = 0, HudCorner? corner = null,
                                         float minWidth = 0f, int fontSize = 0)
        {
            if (string.IsNullOrEmpty(name) || build == null) return null;

            var handle = new HudHandle(name, build, order, corner, minWidth, fontSize);

            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Name != name) continue;
                FruitHudNative.Forget(_panels[i]);
                _panels[i] = handle;
                Sort();
                return handle;
            }

            _panels.Add(handle);
            Sort();
            FruitLog.Info($"[FruitLib] HUD panel registered: {name}");
            return handle;
        }

        public static void Unregister(string name)
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Name != name) continue;
                FruitHudNative.Forget(_panels[i]);
                _panels.RemoveAt(i);
                return;
            }
        }

        // ── Visibility ────────────────────────────────────────────────────────

        public static bool Visible
        {
            get => FruitLibConfig.Enabled && !_userHidden;
            set => _userHidden = !value;
        }

        public static void SetVisible(string name, bool visible)
        {
            var h = Find(name);
            if (h != null) h.Visible = visible;
        }

        public static bool IsVisible(string name) => Find(name)?.Visible ?? false;

        public static HudHandle Find(string name)
        {
            for (int i = 0; i < _panels.Count; i++)
                if (_panels[i].Name == name) return _panels[i];
            return null;
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private static readonly List<HudHandle> _panels = new List<HudHandle>();
        private static bool _userHidden;

        internal static IReadOnlyList<HudHandle> Panels => _panels;

        private static void Sort() => _panels.Sort((a, b) =>
        {
            int c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        /// <summary>Whether anything should be on screen at all this frame.</summary>
        internal static bool ShouldDraw => !_drawBroken && Visible && !FruitMenu.IsOpen && _panels.Count > 0;

        // ── FruitLibMod hooks (called automatically) ──────────────────────────

        internal static void Init()
        {
            FruitLibConfig.EnsureLoaded();
            FruitMenu.Register("FruitLib", FruitLibConfig.IniPath, typeof(FruitLibConfig), FruitLibConfig.Write);
        }

        internal static void Tick()
        {
            if (!FruitMenu.IsInputSuppressed &&
                FruitLibConfig.ToggleKey != KeyCode.None &&
                Input.GetKeyDown(FruitLibConfig.ToggleKey))
                _userHidden = !_userHidden;

            // The native HUD lays out and draws in Update; IMGUI does it in OnGUI below.
            try { FruitHudNative.Tick(); }
            catch (Exception e) { FruitHudNative.Fail("tick", e); }
        }

        internal static void Draw()
        {
            if (FruitHudNative.Active || !ShouldDraw) return;
            if (Event.current.type != EventType.Repaint) return;

            try { DrawImgui(); }
            catch (Exception e)
            {
                _drawBroken = true;
                MelonLogger.Error($"[FruitLib] HUD disabled after an unrecoverable draw error: {e}");
            }
        }

        private static bool _drawBroken;

        // ── Layout, shared by both backends ───────────────────────────────────

        /// <summary>Where one panel goes this frame, in top-left-origin screen units.</summary>
        internal struct Placement
        {
            public HudHandle Handle;
            public float X, Y, BoxW, BoxH, TextW, LineH;
            public int   FontSize;
        }

        internal const float PadX = 10f;
        internal const float PadY = 6f;

        /// <summary>
        /// Builds every visible panel and stacks it into its corner.
        ///
        /// <paramref name="measure"/> returns a row's width at a font size, and is only asked
        /// again when a panel's rows change. <paramref name="lineHeight"/> maps a font size to
        /// a row height. Both belong to the backend, since IMGUI and TextMeshPro set the same
        /// text at different widths.
        /// </summary>
        internal static void Layout(float screenW, float screenH, List<Placement> into,
                                    Func<string, int, bool, float> measure, Func<int, float> lineHeight)
        {
            into.Clear();

            float mx = FruitLibConfig.MarginX;
            float my = FruitLibConfig.MarginY;
            float gap = FruitLibConfig.Gap;

            float cTL = my, cTR = my;
            float cBL = screenH - my, cBR = screenH - my;

            for (int i = 0; i < _panels.Count; i++)
            {
                var e = _panels[i];
                if (!e.Visible || e.Failed) continue;

                e.Panel.Reset();
                try { e.Build(e.Panel); }
                catch (Exception ex)
                {
                    e.Failed = true;
                    MelonLogger.Warning($"[FruitLib] HUD panel '{e.Name}' threw and was disabled: {ex}");
                    continue;
                }

                var rows = e.Panel.Rows;
                if (rows.Count == 0) continue;

                int   fs    = e.FontSize > 0 ? e.FontSize : FruitLibConfig.FontSize;
                float lineH = lineHeight(fs);

                if (e.MeasuredAt != fs || Changed(e.Measured, rows))
                {
                    float maxW = 0f;
                    for (int r = 0; r < rows.Count; r++)
                    {
                        if (rows[r].Rule || string.IsNullOrEmpty(rows[r].Text)) continue;
                        float w = measure(rows[r].Text, fs, rows[r].Bold);
                        if (w > maxW) maxW = w;
                    }
                    e.MaxW       = maxW;
                    e.MeasuredAt = fs;
                    e.Measured.Clear();
                    e.Measured.AddRange(rows);
                }

                float textW = Mathf.Max(e.MaxW, e.MinWidth);
                float boxW  = textW + PadX * 2f;
                float boxH  = rows.Count * lineH + PadY * 2f;

                HudCorner corner = e.Corner ?? GlobalCorner;
                float x, y;
                switch (corner)
                {
                    case HudCorner.TopRight:
                        x = screenW - boxW - mx; y = cTR; cTR += boxH + gap; break;
                    case HudCorner.BottomLeft:
                        x = mx;                  y = cBL - boxH; cBL -= boxH + gap; break;
                    case HudCorner.BottomRight:
                        x = screenW - boxW - mx; y = cBR - boxH; cBR -= boxH + gap; break;
                    default:
                        x = mx;                  y = cTL; cTL += boxH + gap; break;
                }

                into.Add(new Placement
                {
                    Handle = e, X = x, Y = y, BoxW = boxW, BoxH = boxH,
                    TextW = textW, LineH = lineH, FontSize = fs,
                });
            }
        }

        /// <summary>A backend switch measures text differently; start the width cache over.</summary>
        internal static void ForgetMeasurements()
        {
            foreach (var p in _panels) { p.Measured.Clear(); p.MeasuredAt = -1; }
        }

        private static HudCorner GlobalCorner =>
            (HudCorner)Mathf.Clamp(FruitLibConfig.Corner, 0, 3);

        private static bool Changed(List<HudPanel.Row> prev, List<HudPanel.Row> now)
        {
            if (prev.Count != now.Count) return true;
            for (int i = 0; i < now.Count; i++)
            {
                if (prev[i].Text == now[i].Text &&
                    prev[i].Bold == now[i].Bold &&
                    prev[i].Rule == now[i].Rule) continue;
                return true;
            }
            return false;
        }

        // ── IMGUI backend (fallback) ──────────────────────────────────────────

        private static readonly List<Placement> _imguiPlaced = new List<Placement>();
        private static readonly Color C_Rule = new Color(0.45f, 0.45f, 0.45f, 0.8f);

        private static void DrawImgui()
        {
            var savedColor   = GUI.color;
            var savedContent = GUI.contentColor;
            GUI.color        = Color.white;
            GUI.contentColor = Color.white;

            try
            {
                Layout(Screen.width, Screen.height, _imguiPlaced,
                       (text, fs, bold) => Style(fs, bold).CalcSize(new GUIContent(text)).x,
                       fs => Mathf.Ceil(fs * 1.45f));

                foreach (var p in _imguiPlaced)
                {
                    FruitMenu.Rect(p.X, p.Y, p.BoxW, p.BoxH, new Color(0f, 0f, 0f, FruitLibConfig.BgAlpha));

                    var rows = p.Handle.Panel.Rows;
                    float ly = p.Y + PadY;
                    for (int r = 0; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        if (row.Rule)
                            FruitMenu.Rect(p.X + PadX, ly + p.LineH * 0.5f, p.TextW, 1f, C_Rule);
                        else if (!string.IsNullOrEmpty(row.Text))
                        {
                            GUI.contentColor = row.Color;
                            GUI.Label(new Rect(p.X + PadX, ly, p.TextW, p.LineH), row.Text, Style(p.FontSize, row.Bold));
                            GUI.contentColor = Color.white;
                        }
                        ly += p.LineH;
                    }
                }
            }
            finally
            {
                GUI.color        = savedColor;
                GUI.contentColor = savedContent;
            }
        }

        private static readonly Dictionary<int, GUIStyle> _plain = new Dictionary<int, GUIStyle>();
        private static readonly Dictionary<int, GUIStyle> _bold  = new Dictionary<int, GUIStyle>();

        private static GUIStyle Style(int fontSize, bool bold)
        {
            var cache = bold ? _bold : _plain;
            if (cache.TryGetValue(fontSize, out var s)) return s;

            s = FruitMenu.WithText(new GUIStyle
            {
                padding = new RectOffset(0, 0, 0, 0),
                normal  = { textColor = Color.white }
            }, fontSize, bold, FruitMenu.TextAlign.MiddleLeft);

            cache[fontSize] = s;
            return s;
        }
    }

    // ── Panel handle ──────────────────────────────────────────────────────────
    public sealed class HudHandle
    {
        public readonly string Name;

        public bool Visible = true;

        public void Unregister() => FruitHud.Unregister(Name);

        internal readonly Action<HudPanel>   Build;
        internal readonly int                Order;
        internal readonly HudCorner?         Corner;
        internal readonly float              MinWidth;
        internal readonly int                FontSize;

        internal readonly HudPanel           Panel    = new HudPanel();
        internal readonly List<HudPanel.Row> Measured = new List<HudPanel.Row>();
        internal float MaxW;
        internal int   MeasuredAt = -1;   // font size the widths in MaxW were measured at
        internal bool  Failed;

        internal HudHandle(string name, Action<HudPanel> build, int order,
                           HudCorner? corner, float minWidth, int fontSize)
        {
            Name = name; Build = build; Order = order;
            Corner = corner; MinWidth = minWidth; FontSize = fontSize;
        }
    }

    // ── Panel content ─────────────────────────────────────────────────────────
    public sealed class HudPanel
    {
        public static readonly Color Normal = Color.white;
        public static readonly Color Dim    = new Color(0.62f, 0.62f, 0.62f, 1f);
        public static readonly Color Good   = new Color(0.45f, 0.85f, 0.45f, 1f);
        public static readonly Color Warn   = new Color(0.95f, 0.75f, 0.30f, 1f);
        public static readonly Color Bad    = new Color(0.90f, 0.40f, 0.40f, 1f);

        internal struct Row
        {
            public string Text;
            public Color  Color;
            public bool   Bold;
            public bool   Rule;
        }

        internal readonly List<Row> Rows = new List<Row>();
        internal void Reset() => Rows.Clear();

        public void Line(string text) => Line(text, Normal);

        public void Line(string text, Color color) =>
            Rows.Add(new Row { Text = text, Color = color });

        public void Header(string text) => Header(text, new Color(0.85f, 0.85f, 0.85f, 1f));

        public void Header(string text, Color color) =>
            Rows.Add(new Row { Text = text, Color = color, Bold = true });

        public void Separator() => Rows.Add(new Row { Rule = true });

        public void Blank() => Rows.Add(new Row { Text = "" });
    }
}
