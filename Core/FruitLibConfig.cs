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
    /// <summary>
    /// FruitLib's own settings: the HUD, the performance overlay, update checks, the shared
    /// ballistics effects and diagnostics. Shown as the FruitLib entry of the MODS menu and
    /// stored in UserData/FruitLibConfig.ini.
    /// </summary>
    public static class FruitLibConfig
    {
        // Declaration order is menu order: categories list in the order their first field
        // appears, so gameplay first and Diagnostics last. Fields with no [MenuCategory] are
        // ini-only (the "Advanced" section of the file): fine-tuning nobody needs in the menu.

        // ── Ballistics ──

        [MenuCategory("Ballistics"), MenuLabel("Rounds go through walls")]
        public static bool WallPenetration = true;

        [MenuCategory("Ballistics"), MenuLabel("Penetration strength"), MenuRange(0.1f, 3f)]
        public static float PenetrationScale = 1f;

        [MenuCategory("Ballistics"), MenuLabel("Walls shield from blasts")]
        public static bool BlastOcclusion = true;

        // ── Wound effects ──

        [MenuCategory("Wound effects"), MenuLabel("Exit wound ejecta")]
        public static bool Ejecta = true;

        [MenuCategory("Wound effects"), MenuLabel("Show chunks")]
        public static bool EjectaVisible = true;

        [MenuCategory("Wound effects"), MenuLabel("Chunks per wound"), MenuRange(0, 40)]
        public static int EjectaMaxCount = 12;

        [MenuCategory("Wound effects"), MenuLabel("Chunk size (m)"), MenuRange(0.01f, 0.2f)]
        public static float EjectaSize = 0.05f;

        [MenuCategory("Wound effects"), MenuLabel("Chunk lifetime"), MenuRange(0, 30)]
        public static float EjectaLifetime = 3f;

        [MenuCategory("Wound effects"), MenuLabel("Blood decals")]
        public static bool BloodDecals = true;

        [MenuCategory("Wound effects"), MenuLabel("Decal size"), MenuRange(0.02f, 1)]
        public static float BloodDecalSize = 0.15f;

        [MenuCategory("Wound effects"), MenuLabel("Decal lifetime"), MenuRange(0, 60)]
        public static float BloodDecalLifetime = 5f;

        // ── HUD ──

        [MenuCategory("HUD"), MenuLabel("Show mod panels")]
        public static bool Enabled = true;

        [MenuCategory("HUD"), MenuLabel("Show / hide key")]
        public static KeyCode ToggleKey = KeyCode.F8;

        [MenuCategory("HUD"), MenuLabel("Corner"), MenuRange(0, 3)]
        public static int Corner = 0;

        [MenuCategory("HUD"), MenuLabel("Text size"), MenuRange(8, 32)]
        public static int FontSize = 14;

        [MenuCategory("HUD"), MenuLabel("Background opacity"), MenuRange(0, 1)]
        public static float BgAlpha = 0.55f;

        // ── Performance, updates ──

        [MenuCategory("Performance"), MenuLabel("Target frame rate"), MenuRange(20, 240)]
        public static float PerfTargetFps = 60f;

        [MenuCategory("Updates"), MenuLabel("Check for mod updates")]
        public static bool CheckForUpdates = true;

        // ── Diagnostics: last ──

        [MenuCategory("Diagnostics"), MenuLabel("Verbose log")]
        public static bool VerboseLog = false;

        [MenuCategory("Diagnostics"), MenuLabel("Performance overlay key (shift: next view, ctrl: reset)")]
        public static KeyCode PerfKey = KeyCode.F11;

        [MenuCategory("Diagnostics"), MenuLabel("Native HUD (off = IMGUI fallback)")]
        public static bool NativeHud = true;

        [MenuCategory("Diagnostics"), MenuLabel("Log ballistics")]
        public static bool BallisticsProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Ballistics aim test key")]
        public static KeyCode BallisticsProbeKey = KeyCode.F6;

        [MenuCategory("Diagnostics"), MenuLabel("Debug hooks + crash trace (restart)")]
        public static bool DebugHooks = false;

#if FRUITLIB_DEVTOOLS
        [MenuCategory("Diagnostics"), MenuLabel("Log pause menu transitions")]
        public static bool MenuProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Dump menu structure key")]
        public static KeyCode MenuProbeKey = KeyCode.F7;

        [MenuCategory("Diagnostics"), MenuLabel("Test asset bundles")]
        public static bool BundleProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Bundle test key")]
        public static KeyCode BundleProbeKey = KeyCode.F10;
#endif

        // ── Ini-only ──

        [MenuLabel("Margin, across"), MenuRange(0, 200)]
        public static float MarginX = 12f;

        [MenuLabel("Margin, down"), MenuRange(0, 200)]
        public static float MarginY = 12f;

        [MenuLabel("Space between panels"), MenuRange(0, 40)]
        public static float Gap = 6f;

        [MenuLabel("Ejecta min. depth (voxels)"), MenuRange(1, 20)]
        public static int EjectaMinDepth = 3;

        [MenuLabel("Power for full chunk count"), MenuRange(1000, 100000)]
        public static int EjectaFullPower = 15000;

        [MenuLabel("Chunk speed"), MenuRange(0, 20)]
        public static float EjectaSpeed = 5f;

        [MenuLabel("Chunk spread"), MenuRange(0, 1)]
        public static float EjectaSpread = 0.35f;

        [MenuLabel("Decals per chunk"), MenuRange(0, 12)]
        public static int BloodDecalCount = 4;

        [MenuLabel("Blood atlas columns"), MenuRange(1, 8)]
        public static int BloodAtlasCols = 3;

        [MenuLabel("Blood atlas rows"), MenuRange(1, 8)]
        public static int BloodAtlasRows = 3;

        [MenuLabel("Effects target FPS"), MenuRange(20, 240)]
        public static float EjectaTargetFps = 75f;

        [MenuLabel("Effects cull speed"), MenuRange(0, 2)]
        public static float EjectaCullSpeed = 0.4f;

        public static string IniPath => FruitPaths.Config("FruitLibConfig.ini", typeof(FruitLibConfig).Assembly);

        private static readonly Dictionary<string, string> Help = new Dictionary<string, string>
        {
            ["Enabled"]   = "master switch for every mod HUD panel FruitLib draws",
            ["NativeHud"] = "draws mod panels with the game's own UI and HUD font. Off = the older IMGUI panels. Switches over live",
            ["ToggleKey"] = "hides / shows all mod HUD panels for this session (None to disable the key)",
            ["Corner"]    = "where the stack sits: 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right. Panels that pin themselves (FruitPerfMon) ignore this",
            ["MarginX"]   = "distance from the left or right screen edge, in pixels",
            ["MarginY"]   = "distance from the top or bottom screen edge, in pixels",
            ["Gap"]       = "vertical space between stacked panels, in pixels",
            ["FontSize"]  = "HUD text size in points; row height follows it",
            ["BgAlpha"]   = "panel background opacity, 0 = fully transparent",
            ["PerfKey"]         = "shows / hides the performance overlay. With Shift it steps through the views (all mods, then each mod in full); with Ctrl it resets the peaks on screen (None to disable)",
            ["PerfTargetFps"]   = "frame rate below which FruitPerfMon reports load, and effects (ejecta, blast quality) start scaling back",
            ["CheckForUpdates"] = "checks each mod's GitHub repo at launch and warns if a newer release is out",
            ["VerboseLog"]   = "writes FruitLib's (and opted-in mods') informational lines to the console: menu pages built, items registered and equipped, panels added. Warnings and errors always print. Takes effect immediately",
            ["DebugHooks"]   = "installs FruitLib's diagnostic Harmony hooks (equip-path tracing) and writes UserData/FruitLib_trace.log. Read once at startup: restart the game after changing it. Off costs nothing",
#if FRUITLIB_DEVTOOLS
            ["MenuProbe"]    = "logs how the pause menu moves between its screens - structure once, then a line a frame while anything is animating. For working on the menu; noisy otherwise",
            ["MenuProbeKey"] = "dumps the pause menu structure again on demand while MenuProbe is on (None to disable the key)",
#endif
            ["WallPenetration"]    = "FruitLib rounds and explosion fragments with enough energy go through walls, crates and props instead of stopping, slowing down and tumbling as they do. How far depends on the round and the material. Off = they only ricochet or stop",
            ["PenetrationScale"]   = "multiplies how well every FruitLib round and fragment goes through surfaces. 1 = physical (a 9 mm through ~13 cm of wood, a 7.62x39 through ~36 cm, a 2 g grenade fragment through ~6 cm)",
            ["BlastOcclusion"]     = "walls between an explosion and a body cut its shockwave and overpressure: concrete and steel stop nearly all of it, wood and drywall let some through. A blast still spills round cover a little. Off = blasts reach everything in range as before",
            ["Ejecta"]             = "tissue-coloured chunks thrown out of exit wounds by every FruitLib projectile and fragment. Master switch; a mod can also turn it off per weapon",
            ["EjectaMinDepth"]     = "a wound only throws ejecta if the round came out the far side after at least this many voxels (~23 mm each). Stops grazes and corner nicks from spraying chunks",
            ["EjectaFullPower"]    = "power a round must still carry out of an exit wound to throw the full chunk count; less throws proportionally fewer (at least one). A rifle round is ~15000, a buckshot pellet ~2100",
            ["EjectaVisible"]      = "draws the chunks. Off = they still fly, stick and leave blood decals, but are not seen",
            ["EjectaMaxCount"]     = "most chunks one exit wound throws; fewer under frame pressure",
            ["EjectaSize"]         = "edge length of one chunk, metres. A voxel is ~0.023",
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
#if FRUITLIB_DEVTOOLS
            ["BundleProbe"]        = "loads every *.bundle in UserData/FruitBundles on the key, logs its contents and spawns its first prefab in front of you. For building custom models; off otherwise",
            ["BundleProbeKey"]     = "while BundleProbe is on: runs the bundle test. With Shift held, spawned materials use the game's shaders instead of the bundle's (None to disable the key)",
#endif
        };

        private static bool _loaded;

        /// <summary>Loads once. Harmony can ask for DebugHooks while applying patches, which may
        /// be before FruitLib's own OnInitializeMelon.</summary>
        internal static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            // Before the ini is read, so Reset to Defaults goes back to the code's values.
            // Straight to FruitSetting: this can run from Harmony's patching, before FruitMenu is needed.
            FruitSetting.CaptureDefaults(typeof(FruitLibConfig));
            Load();
        }

        private static void Load()
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
            var f = typeof(FruitLibConfig).GetField(key, BindingFlags.Public | BindingFlags.Static);
            if (f == null) return;
            try
            {
                if      (f.FieldType == typeof(float))   f.SetValue(null, float.Parse(value, CultureInfo.InvariantCulture));
                else if (f.FieldType == typeof(int))     f.SetValue(null, int.Parse(value, CultureInfo.InvariantCulture));
                else if (f.FieldType == typeof(bool))    f.SetValue(null, value.ToLower() == "true");
                else if (f.FieldType == typeof(KeyCode)) f.SetValue(null, (KeyCode)Enum.Parse(typeof(KeyCode), value, true));
            }
            catch { }
        }

        /// <summary>Writes the ini sectioned, with help. The one writer of this file: the menu saves through it too.</summary>
        internal static void Write()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# FruitLib - shared settings for every FruitLib mod");
                sb.AppendLine("# Also editable in-game: pause menu > MODS > FRUITLIB.");
                sb.AppendLine();

                var order      = new List<string>();
                var byCategory = new Dictionary<string, List<FieldInfo>>();
                foreach (var f in typeof(FruitLibConfig).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.IsSpecialName) continue;
                    var attr = (MenuCategoryAttribute)Attribute.GetCustomAttribute(f, typeof(MenuCategoryAttribute));
                    string cat = attr?.Name ?? "Advanced";
                    if (!byCategory.TryGetValue(cat, out var list))
                    {
                        byCategory[cat] = list = new List<FieldInfo>();
                        order.Add(cat);
                    }
                    list.Add(f);
                }

                foreach (var cat in order)
                {
                    sb.AppendLine($"# ── {cat} ──");
                    foreach (var f in byCategory[cat])
                    {
                        if (Help.TryGetValue(f.Name, out var help))
                            sb.AppendLine($"# {f.Name} : {help}");
                        object val = f.GetValue(null);
                        string s = f.FieldType == typeof(float)
                            ? ((float)val).ToString("0.##############", CultureInfo.InvariantCulture)
                            : val?.ToString() ?? "";
                        sb.AppendLine($"{f.Name} = {s}");
                    }
                    sb.AppendLine();
                }

                FruitPaths.WriteAllTextAtomic(IniPath, sb.ToString());
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitLib] Config write failed: {e.Message}"); }
        }
    }
}
