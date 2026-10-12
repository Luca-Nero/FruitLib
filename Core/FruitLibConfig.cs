﻿using System;
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

        // ── Bodies ──

        [MenuCategory("Bodies"), MenuLabel("Help bodies up steps")]
        public static bool StepAssist = true;

        [MenuCategory("Bodies"), MenuLabel("Step when turning on the spot")]
        public static bool StepTurns = true;

        /// <summary>Forward move direction (0..1) a stepping turn holds, ini-only. 0.25 shuffles, 0.6 takes real steps. Since 5.32.0.</summary>
        public static float StepTurnPace = 0.6f;

        /// <summary>Widest navmesh box FruitNav builds, metres (ini-only).</summary>
        public static float NavMaxSize = 240f;

        /// <summary>Extra room shaped paths keep from walls, metres (ini-only). Since 5.21.0.</summary>
        public static float NavPathMargin = 0.3f;

        /// <summary>Wobble-aware path following (ini-only; off = 5.20 walking). Since 5.21.0.</summary>
        public static bool NavSmoothWalking = true;

        /// <summary>Width-aware steering (ini-only): the lookahead shrinks where cutting a corner would
        /// take the body further into the margin than the path has room, and a body that has strayed
        /// off the walkable area is pushed back onto it. Since 5.33.0.</summary>
        public static bool NavWidthAware = true;

        /// <summary>Tallest step the navmesh treats as walkable with the step assist on, metres (ini-only;
        /// 0.1 to 0.32). The assist lifts up to 32 cm and the navmesh's 10 cm cells blur a ledge's height
        /// by one cell, so 0.30 let paths run over 30-35 cm ledges the body then stopped at. Since 5.33.0.</summary>
        public static float NavClimb = 0.25f;

        /// <summary>How far from an item a body stands to pick it up, metres from its pelvis to the
        /// nearest point of the item: for one it reaches standing, and for one it crouches to (ini-only).
        /// Since 5.33.0.</summary>
        public static float PickupStance = 0.45f;
        public static float PickupStanceLow = 0.45f;

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

        [MenuCategory("Diagnostics"), MenuLabel("Sight test (eye camera, world names)")]
        public static bool SightProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Sight test key (shift: every body)")]
        public static KeyCode SightProbeKey = KeyCode.F10;

        [MenuCategory("Diagnostics"), MenuLabel("Show navmesh and paths")]
        public static bool NavDraw = false;

        [MenuCategory("Diagnostics"), MenuLabel("Pathing test (paths, walking course)")]
        public static bool NavProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Pathing test key (shift: send, ctrl: course, alt: stairs)")]
        public static KeyCode NavProbeKey = KeyCode.F5;

        [MenuCategory("Diagnostics"), MenuLabel("Hands test (holding, guns, animation)")]
        public static bool HandsProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Hands test key (shift: bar, ctrl: use/throw/give, alt: animation)")]
        public static KeyCode HandsProbeKey = KeyCode.Insert;

        [MenuCategory("Diagnostics"), MenuLabel("Gait test (walks one body up and down a line)")]
        public static bool GaitProbe = false;

        [MenuCategory("Diagnostics"), MenuLabel("Gait test key (shift: stop)")]
        public static KeyCode GaitProbeKey = KeyCode.Home;

        [MenuCategory("Diagnostics"), MenuLabel("Log world events to file")]
        public static bool EventLog = false;

        [MenuCategory("Diagnostics"), MenuLabel("World events panel")]
        public static bool EventPanel = false;

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

        [MenuLabel("Most rounds in flight"), MenuRange(64, 16384)]
        public static int MaxLiveRounds = 4096;

        [MenuLabel("Body samples per second"), MenuRange(0.5f, 30f)]
        public static float BodySampleHz = 5f;

        [MenuLabel("Event log body snapshot (s)"), MenuRange(1f, 60f)]
        public static float EventLogSnapshotSeconds = 5f;

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
            ["EventLog"]     = "writes every world event (FruitEvents: explosions, wounds, pain spikes, falls, deaths, mod events) to UserData/FruitEvents.log, with a snapshot of every tracked body every few seconds. Turns body tracking on while set",
            ["EventPanel"]   = "HUD panel with one line per tracked body (behaviour, posture, cognition, pain, blood) and the newest world events. Turns body tracking on while set",
            ["BodySampleHz"] = "how often each tracked body's vitals, state and limbs are read, per second. Higher = events land sooner, at more cost per body",
            ["EventLogSnapshotSeconds"] = "seconds between body snapshots in FruitEvents.log",
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
            ["MaxLiveRounds"]      = "most FruitLib rounds in flight at once, every mod's together; past it the oldest is ended. A flechette rocket alone throws over a thousand (5.9.0; was a fixed 512)",
            ["BallisticsProbe"]    ="logs the game's native bullet model: calibre constants once per scene, then power spent, channel and exit tear for every native shot that hits a body. For building the shared projectile API; noisy otherwise",
            ["SightProbe"]         = "test for NeuroFrukt's sight: on the key, captures what the body under the crosshair sees through an eye camera (two ways, timed, saved to UserData/FruitSight/) and logs what rays from its eyes hit, who it can see and which loose objects are near, to UserData/FruitSight.log. Also shows the player's orb to those captures. Off otherwise",
            ["SightProbeKey"]      = "while SightProbe is on: tests the body under the crosshair; with Shift, every tracked body one after another (None to disable the key)",
            ["StepTurns"]          = "a body ordered to turn on the spot (face someone, look around, pivot at a corner) walks a tight arc on its own steps while it turns, instead of twisting with both feet planted and dragging them. Costs a drift of about 20 cm per 120 degrees. Since 5.32.0",
            ["StepTurnPace"]       = "how hard a stepping turn walks, 0 to 1: 0.25 shuffles, 0.4 small steps, 0.6 (default) real steps with the feet lifted about 9 cm, more drifts further. Measured on 120-degree turns by the GaitProbe",
            ["StepAssist"]         = "bodies walking under a mod's orders get a lift up kerbs, thresholds and stairs (up to 30 cm): the game's walk barely raises the feet, so without it any step stops them and pathing treats steps as walls",
            ["NavMaxSize"]         = "widest box, in metres, FruitNav builds a navmesh in. It covers you and every body; past this it is centred on you",
            ["NavPathMargin"]      = "room, in metres, the paths bodies walk keep from walls where there is that much, on top of the 0.38 m every path keeps. Narrower passages are walked down the middle. 0 = hug corners as the navmesh's shortest path does",
            ["NavWidthAware"]      = "bodies walking paths take corners no deeper than the path has room for (a shorter lookahead there) and are pushed back toward the walkable area when they have swayed out of it, so shoulders stop catching door frames and wall corners. False = the 5.32 steering",
            ["NavClimb"]           = "tallest step, in metres, the navmesh treats as walkable while the step assist is on (0.1 to 0.32). Lower than the assist's own 0.32 limit on purpose: ledges of 30-35 cm used to be walkable on the navmesh and not for the body, which stood against them. Applies from the next scene",
            ["PickupStance"]       = "how far from an item, in metres from its nearest point to the body's pelvis, a body stands to pick it up when it can reach it standing. Standing right over it breaks the reach animation",
            ["PickupStanceLow"]    = "the same for an item low enough that the body crouches to it: further, because the knees come forward in the crouch",
            ["NavSmoothWalking"]   ="bodies walk paths shaped to keep off walls, steer from where their body is on average instead of each sway of the pelvis, slow down for corners and tight spots, and turn on the spot at sharp corners. False = the 5.20 walking (shortest paths, steered from the pelvis)",
            ["NavDraw"]            ="draws FruitNav's navmesh within 50 m (teal, cyan edges) and the path every walking body follows (yellow; orange through tight spots; red where it can't get all the way). Turns FruitNav on while set",
            ["NavProbe"]           = "test for pathing: runs and draws FruitNav and measures every body walking a path (how far off it, how much it sways, how near the walls). On the key, rebuilds the navmesh and logs every body's path to the point under the crosshair; Shift + key sends them all there; Ctrl + key builds a walking course on open ground at the crosshair (about 15 x 36 m, ahead and to the right), then each press runs every body through it, half walking the 5.20 way and half the 5.21 way, and logs a comparison (Ctrl+Shift removes it); Alt + key builds a stair or ramp climb in front of the body you look at (Alt+Shift removes it). Logs to UserData/FruitNav.log. Off otherwise",
            ["NavProbeKey"]        = "while NavProbe is on: logs paths to the crosshair. Shift: send every body there. Ctrl: walking course. Alt: stair test (None to disable the key)",
            ["HandsProbe"]         = "test for bodies holding things: on the key, the nearest standing body walks to the item under the crosshair and picks it up (a body you look at that holds something drops it). Shift + key: a steel bar for the body you look at, 0.5, 2, 5 and 15 kg in turn. Ctrl + key: every holder uses what it holds on the crosshair (a gun is aimed and fired, anything else is swung). Ctrl+Shift: holders throw it at the crosshair. Ctrl+Alt: holders hand it to the nearest other body. Alt + key: the body you look at goes into the next pose (crouch, kneel, hands up), then back to standing, traced 4 times a second. Ctrl+Alt+Shift: plays the next animation clip the game has loaded on that body. Alt+Shift: drop everything, remove the bars. Also adds Hand to / Make drop lines to items' right-click menus. Logs to UserData/FruitHands.log. Off otherwise",
            ["HandsProbeKey"]      = "while HandsProbe is on: fetch or drop. Shift: test bar. Ctrl: use. Ctrl+Shift: throw. Ctrl+Alt: give. Alt: next pose. Ctrl+Alt+Shift: animation. Alt+Shift: clear (None to disable the key)",
            ["GaitProbe"]          = "test for how a body walks: on the key, the body under the crosshair walks a straight 9 m line along where you look, back and forth, from standing: the game's own walk with nothing steering it, FruitBodyControl as it is, and with a persistent yaw command, twice each, after 4 s of standing still. Every physics step (pelvis, feet, the animator's rig and the muscles' rig, yaw, move direction, animation speed, balance) goes to UserData/FruitGait.csv and a summary per run to the console. Turn NeuroFrukt's walking off while it runs. Off otherwise",
            ["GaitProbeKey"]       = "while GaitProbe is on: starts the test on the body under the crosshair. Shift: stop (None to disable the key)",
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
