using FruitLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace MyMod
{
    // ══════════════════════════════════════════════════════════════════════════════
    // The ini behind Config. Copy this file, change the namespace, FileName and Title,
    // and fill FieldHelp. Everything else is generic over Config's public static fields.
    //
    // - Lives in MelonLoader's UserData (FruitPaths.Config), and moves an older copy
    //   across from Mods the first time.
    // - Written sectioned by [MenuCategory], with each field's help above it. Fields without
    //   a category are ini-only: kept in the file, never drawn in the menu.
    // - Rewritten on every load, so new fields appear and removed ones drop out while
    //   the player's values are kept.
    //
    // Register Write with FruitMenu (see Core.cs) and the menu saves through it too, so the
    // file keeps its sections and comments. Only call this after FruitGate.Check passed.
    // ══════════════════════════════════════════════════════════════════════════════
    internal static class ConfigLoader
    {
        private const string FileName = "MyModConfig.ini";
        private const string Title    = "MyMod";

        public static string IniPath => FruitPaths.Config(FileName, typeof(ConfigLoader).Assembly);

        /// <summary>One line per field, shown above it in the ini. Keyed by field name.</summary>
        private static readonly Dictionary<string, string> FieldHelp = new Dictionary<string, string>
        {
            ["Enabled"]  = "master switch",
            ["Strength"] = "how hard it pushes, in newtons",
            ["UseKey"]   = "key that triggers it (None to disable)",
        };

        public static void Load()
        {
            try
            {
                if (File.Exists(IniPath))
                {
                    foreach (var line in File.ReadAllLines(IniPath))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        int eq = t.IndexOf('=');
                        if (eq < 0) continue;
                        SetField(t.Substring(0, eq).Trim(), t.Substring(eq + 1).Trim());
                    }
                }
                Write();
                FruitLog.Info($"{FileName} loaded.");
            }
            catch (Exception e) { MelonLogger.Warning($"{FileName}: load failed, using defaults: {e.Message}"); }
        }

        private static void SetField(string key, string value)
        {
            var f = typeof(Config).GetField(key, BindingFlags.Public | BindingFlags.Static);
            if (f == null) return;   // a setting this version no longer has
            try
            {
                if      (f.FieldType == typeof(float))   f.SetValue(null, float.Parse(value, CultureInfo.InvariantCulture));
                else if (f.FieldType == typeof(int))     f.SetValue(null, int.Parse(value, CultureInfo.InvariantCulture));
                else if (f.FieldType == typeof(bool))    f.SetValue(null, value.Equals("true", StringComparison.OrdinalIgnoreCase));
                else if (f.FieldType == typeof(string))  f.SetValue(null, value);
                else if (f.FieldType == typeof(KeyCode)) f.SetValue(null, (KeyCode)Enum.Parse(typeof(KeyCode), value, true));
            }
            catch { MelonLogger.Warning($"{FileName}: '{key} = {value}' is not a valid {f.FieldType.Name}; keeping the default."); }
        }

        private static bool IsStored(Type t) =>
            t == typeof(bool) || t == typeof(float) || t == typeof(int) ||
            t == typeof(string) || t == typeof(KeyCode);

        internal static void Write()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {Title} - configuration");
            sb.AppendLine("# Also editable in-game: pause menu > MODS. Floats use . as the decimal separator.");
            sb.AppendLine();

            var order      = new List<string>();
            var byCategory = new Dictionary<string, List<FieldInfo>>();

            foreach (var f in typeof(Config).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.IsSpecialName || f.IsLiteral || f.IsInitOnly || !IsStored(f.FieldType)) continue;

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
                    if (FieldHelp.TryGetValue(f.Name, out var help)) sb.AppendLine($"# {f.Name} : {help}");
                    sb.AppendLine($"{f.Name} = {Format(f)}");
                }
                sb.AppendLine();
            }

            FruitPaths.WriteAllTextAtomic(IniPath, sb.ToString());
        }

        private static string Format(FieldInfo f)
        {
            object v = f.GetValue(null);
            return f.FieldType == typeof(float)
                ? ((float)v).ToString("0.##############", CultureInfo.InvariantCulture)
                : v?.ToString() ?? "";
        }
    }
}
