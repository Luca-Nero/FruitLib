using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    public enum HudCorner { TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3 }

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
                _panels[i] = handle;
                Sort();
                return handle;
            }

            _panels.Add(handle);
            Sort();
            MelonLogger.Msg($"[FruitLib] HUD panel registered: {name}");
            return handle;
        }

        public static void Unregister(string name)
        {
            for (int i = 0; i < _panels.Count; i++)
            {
                if (_panels[i].Name != name) continue;
                _panels.RemoveAt(i);
                return;
            }
        }

        // ── Visibility ────────────────────────────────────────────────────────

        public static bool Visible
        {
            get => FruitHudConfig.Enabled && !_userHidden;
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

        private static void Sort() => _panels.Sort((a, b) =>
        {
            int c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        // ── FruitLibMod hooks (called automatically) ──────────────────────────

        internal static void Init()
        {
            FruitHudConfig.Load();
            FruitMenu.Register("FruitLib", FruitHudConfig.IniPath, typeof(FruitHudConfig));
        }

        internal static void Tick()
        {
            if (!FruitMenu.IsInputSuppressed &&
                FruitHudConfig.ToggleKey != KeyCode.None &&
                Input.GetKeyDown(FruitHudConfig.ToggleKey))
                _userHidden = !_userHidden;
        }

        internal static void Draw()
        {
            if (_drawBroken || !Visible || FruitMenu.IsOpen || _panels.Count == 0) return;

            if (Event.current.type != EventType.Repaint) return;

            try { DrawPanels(); }
            catch (Exception e)
            {
                _drawBroken = true;
                MelonLogger.Error($"[FruitLib] HUD disabled after an unrecoverable draw error: {e}");
            }
        }

        private static bool _drawBroken;

        private static void DrawPanels()
        {
            EnsureStyles();

            var savedColor   = GUI.color;
            var savedContent = GUI.contentColor;
            GUI.color        = Color.white;
            GUI.contentColor = Color.white;

            try { DrawStack(); }
            finally
            {
                GUI.color        = savedColor;
                GUI.contentColor = savedContent;
            }
        }

        private static void DrawStack()
        {
            float mx = FruitHudConfig.MarginX;
            float my = FruitHudConfig.MarginY;
            float gap = FruitHudConfig.Gap;

            float cTL = my, cTR = my;
            float cBL = Screen.height - my, cBR = Screen.height - my;

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

                int   fs    = e.FontSize > 0 ? e.FontSize : FruitHudConfig.FontSize;
                float lineH = LineHeight(fs);

                if (Changed(e.Measured, rows))
                {
                    float maxW = 0f;
                    for (int r = 0; r < rows.Count; r++)
                    {
                        if (rows[r].Rule || string.IsNullOrEmpty(rows[r].Text)) continue;
                        float w = Style(fs, rows[r].Bold).CalcSize(new GUIContent(rows[r].Text)).x;
                        if (w > maxW) maxW = w;
                    }
                    e.MaxW = maxW;

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
                        x = Screen.width - boxW - mx; y = cTR; cTR += boxH + gap; break;
                    case HudCorner.BottomLeft:
                        x = mx;                       y = cBL - boxH; cBL -= boxH + gap; break;
                    case HudCorner.BottomRight:
                        x = Screen.width - boxW - mx; y = cBR - boxH; cBR -= boxH + gap; break;
                    default:
                        x = mx;                       y = cTL; cTL += boxH + gap; break;
                }

                FruitMenu.Rect(x, y, boxW, boxH, new Color(0f, 0f, 0f, FruitHudConfig.BgAlpha));

                float ly = y + PadY;
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    if (row.Rule)
                    {
                        FruitMenu.Rect(x + PadX, ly + lineH * 0.5f, textW, 1f, C_Rule);
                    }
                    else if (!string.IsNullOrEmpty(row.Text))
                    {
                        GUI.contentColor = row.Color;
                        GUI.Label(new Rect(x + PadX, ly, textW, lineH), row.Text, Style(fs, row.Bold));
                        GUI.contentColor = Color.white;
                    }
                    ly += lineH;
                }
            }
        }

        // ── Layout constants ──────────────────────────────────────────────────

        private const float PadX = 10f;
        private const float PadY = 6f;

        private static readonly Color C_Rule = new Color(0.45f, 0.45f, 0.45f, 0.8f);

        private static HudCorner GlobalCorner =>
            (HudCorner)Mathf.Clamp(FruitHudConfig.Corner, 0, 3);

        private static float LineHeight(int fontSize) => Mathf.Ceil(fontSize * 1.45f);

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


        private static readonly Dictionary<int, GUIStyle> _plain = new Dictionary<int, GUIStyle>();
        private static readonly Dictionary<int, GUIStyle> _bold  = new Dictionary<int, GUIStyle>();
        private static int _styleGen = -1;

        private static void EnsureStyles()
        {
            if (_styleGen == FruitHudConfig.FontSize) return;
            _styleGen = FruitHudConfig.FontSize;
            _plain.Clear();
            _bold.Clear();
            // Cached widths were measured with the old size.
            for (int i = 0; i < _panels.Count; i++) _panels[i].Measured.Clear();
        }

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

    public static class FruitHudConfig
    {
        [MenuCategory("HUD"), MenuLabel("Show mod panels")]
        public static bool Enabled = true;

        [MenuCategory("HUD"), MenuLabel("Show / hide key")]
        public static KeyCode ToggleKey = KeyCode.F8;

        [MenuCategory("HUD"), MenuLabel("Corner"), MenuRange(0, 3)]
        public static int Corner = 0;

        [MenuCategory("HUD"), MenuLabel("Margin, across"), MenuRange(0, 200)]
        public static float MarginX = 12f;

        [MenuCategory("HUD"), MenuLabel("Margin, down"), MenuRange(0, 200)]
        public static float MarginY = 12f;

        [MenuCategory("HUD"), MenuLabel("Space between panels"), MenuRange(0, 40)]
        public static float Gap = 6f;

        [MenuCategory("HUD"), MenuLabel("Text size"), MenuRange(8, 32)]
        public static int FontSize = 14;

        [MenuCategory("HUD"), MenuLabel("Background opacity"), MenuRange(0, 1)]
        public static float BgAlpha = 0.55f;

        [MenuCategory("Updates"), MenuLabel("Check for mod updates")]
        public static bool CheckForUpdates = true;

        [MenuCategory("Diagnostics"), MenuLabel("Log pause menu transitions")]
        public static bool MenuProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Dump menu structure key")]
        public static KeyCode MenuProbeKey = KeyCode.F7;

        [MenuCategory("Ballistics"), MenuLabel("Exit wound ejecta")]
        public static bool Ejecta = true;

        [MenuCategory("Ballistics"), MenuLabel("Ejecta min. depth (voxels)"), MenuRange(1, 20)]
        public static int EjectaMinDepth = 3;

        [MenuCategory("Ballistics"), MenuLabel("Chunks per wound"), MenuRange(0, 40)]
        public static int EjectaMaxCount = 12;

        [MenuCategory("Ballistics"), MenuLabel("Power for full chunk count"), MenuRange(1000, 100000)]
        public static int EjectaFullPower = 15000;

        [MenuCategory("Ballistics"), MenuLabel("Chunk speed"), MenuRange(0, 20)]
        public static float EjectaSpeed = 5f;

        [MenuCategory("Ballistics"), MenuLabel("Chunk spread"), MenuRange(0, 1)]
        public static float EjectaSpread = 0.35f;

        [MenuCategory("Ballistics"), MenuLabel("Chunk lifetime"), MenuRange(0, 30)]
        public static float EjectaLifetime = 3f;

        [MenuCategory("Ballistics"), MenuLabel("Blood decals")]
        public static bool BloodDecals = true;

        [MenuCategory("Ballistics"), MenuLabel("Decals per chunk"), MenuRange(0, 12)]
        public static int BloodDecalCount = 4;

        [MenuCategory("Ballistics"), MenuLabel("Decal size"), MenuRange(0.02f, 1)]
        public static float BloodDecalSize = 0.15f;

        [MenuCategory("Ballistics"), MenuLabel("Decal lifetime"), MenuRange(0, 60)]
        public static float BloodDecalLifetime = 5f;

        [MenuCategory("Ballistics"), MenuLabel("Blood atlas columns"), MenuRange(1, 8)]
        public static int BloodAtlasCols = 3;

        [MenuCategory("Ballistics"), MenuLabel("Blood atlas rows"), MenuRange(1, 8)]
        public static int BloodAtlasRows = 3;

        [MenuCategory("Ballistics"), MenuLabel("Effects target FPS"), MenuRange(20, 240)]
        public static float EjectaTargetFps = 75f;

        [MenuCategory("Ballistics"), MenuLabel("Effects cull speed"), MenuRange(0, 2)]
        public static float EjectaCullSpeed = 0.4f;

        [MenuCategory("Diagnostics"), MenuLabel("Log ballistics")]
        public static bool BallisticsProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Ballistics aim test key")]
        public static KeyCode BallisticsProbeKey = KeyCode.F6;

        [MenuCategory("Diagnostics"), MenuLabel("Test asset bundles")]
        public static bool BundleProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Bundle test key")]
        public static KeyCode BundleProbeKey = KeyCode.F10;

        public static string IniPath => FruitPaths.Config("FruitLibConfig.ini", typeof(FruitHudConfig).Assembly);

        private static readonly Dictionary<string, string> Help = new Dictionary<string, string>
        {
            ["Enabled"]   = "master switch for every mod HUD panel FruitLib draws",
            ["ToggleKey"] = "hides / shows all mod HUD panels for this session (None to disable the key)",
            ["Corner"]    = "where the stack sits: 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right. Panels that pin themselves (FruitPerfMon) ignore this",
            ["MarginX"]   = "distance from the left or right screen edge, in pixels",
            ["MarginY"]   = "distance from the top or bottom screen edge, in pixels",
            ["Gap"]       = "vertical space between stacked panels, in pixels",
            ["FontSize"]  = "HUD text size in points; row height follows it",
            ["BgAlpha"]   = "panel background opacity, 0 = fully transparent",
            ["CheckForUpdates"] = "checks each mod's GitHub repo at launch and warns if a newer release is out",
            ["MenuProbe"]    = "logs how the pause menu moves between its screens - structure once, then a line a frame while anything is animating. For working on the menu; noisy otherwise",
            ["MenuProbeKey"] = "dumps the pause menu structure again on demand while MenuProbe is on (None to disable the key)",
            ["Ejecta"]             = "tissue-coloured chunks thrown out of exit wounds by every FruitLib projectile and fragment. Master switch; a mod can also turn it off per weapon",
            ["EjectaMinDepth"]     = "a wound only throws ejecta if the round came out the far side after at least this many voxels (~23 mm each). Stops grazes and corner nicks from spraying chunks",
            ["EjectaFullPower"]    = "power a round must still carry out of an exit wound to throw the full chunk count; less throws proportionally fewer (at least one). A rifle round is ~15000, a buckshot pellet ~2100",
            ["EjectaMaxCount"]     = "most chunks one exit wound throws; fewer under frame pressure",
            ["EjectaSpeed"]        = "chunk launch speed, m/s",
            ["EjectaSpread"]       = "how far chunks scatter off the bullet's line, 0 = straight on",
            ["EjectaLifetime"]     = "seconds a landed chunk stays before shrinking away",
            ["BloodDecals"]        = "blood splats where chunks land, wrapped onto the surface",
            ["BloodDecalCount"]    = "splats per landed chunk",
            ["BloodDecalSize"]     = "splat spread radius, metres",
            ["BloodDecalLifetime"] = "seconds a splat stays",
            ["BloodAtlasCols"]     = "columns in the game's Pixelblood texture atlas",
            ["BloodAtlasRows"]     = "rows in the game's Pixelblood texture atlas",
            ["EjectaTargetFps"]    = "below this frame rate the oldest chunks and splats are removed early",
            ["EjectaCullSpeed"]    = "how aggressively, per frame of shortfall",
            ["BallisticsProbe"]    ="logs the game's native bullet model: calibre constants once per scene, then power spent, channel and exit tear for every native shot that hits a body. For building the shared projectile API; noisy otherwise",
            ["BallisticsProbeKey"] = "while BallisticsProbe is on: walks the voxels under the crosshair and logs what each would absorb, without damaging anything. With Shift held it also sends the game's own cavitation and exit tear down that path - destructive (None to disable the key)",
            ["BundleProbe"]        = "loads every *.bundle in UserData/FruitBundles on the key, logs its contents and spawns its first prefab in front of you. For building custom models; off otherwise",
            ["BundleProbeKey"]     = "while BundleProbe is on: runs the bundle test. With Shift held, spawned materials use the game's shaders instead of the bundle's (None to disable the key)",
        };

        internal static void Load()
        {
            try
            {
                if (!File.Exists(IniPath)) { Write(); return; }

                foreach (var line in File.ReadAllLines(IniPath))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    SetField(t.Substring(0, eq).Trim(), t.Substring(eq + 1).Trim());
                }
                Write();
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitLib] Config load failed: {e.Message}"); }
        }

        private static void SetField(string key, string value)
        {
            var f = typeof(FruitHudConfig).GetField(key, BindingFlags.Public | BindingFlags.Static);
            if (f == null) return;
            try
            {
                if      (f.FieldType == typeof(float))   f.SetValue(null, float.Parse(value, CultureInfo.InvariantCulture));
                else if (f.FieldType == typeof(int))     f.SetValue(null, int.Parse(value));
                else if (f.FieldType == typeof(bool))    f.SetValue(null, value.ToLower() == "true");
                else if (f.FieldType == typeof(KeyCode)) f.SetValue(null, (KeyCode)Enum.Parse(typeof(KeyCode), value, true));
            }
            catch { }
        }

        private static void Write()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# ╔══════════════════════════════════════════════════════════════╗");
                sb.AppendLine("# ║        FruitLib - shared mod HUD                             ║");
                sb.AppendLine("# ╚══════════════════════════════════════════════════════════════╝");
                sb.AppendLine("# Every mod that registers a HUD panel is stacked into one corner.");
                sb.AppendLine("# Editable in-game under the FruitLib tab of the mod menu.");
                sb.AppendLine();
                sb.AppendLine("# ── HUD ──");

                foreach (var f in typeof(FruitHudConfig).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.IsSpecialName) continue;
                    if (Help.TryGetValue(f.Name, out var help))
                        sb.AppendLine($"# {f.Name} : {help}");
                    object val = f.GetValue(null);
                    string s = f.FieldType == typeof(float)
                        ? ((float)val).ToString("0.##############", CultureInfo.InvariantCulture)
                        : val?.ToString() ?? "";
                    sb.AppendLine($"{f.Name} = {s}");
                }

                File.WriteAllText(IniPath, sb.ToString());
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitLib] Config write failed: {e.Message}"); }
        }
    }
}
