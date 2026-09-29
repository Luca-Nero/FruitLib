using System;
using System.Reflection;
using MelonLoader;

namespace MyMod
{
    // ── FruitLib dependency gate ──────────────────────────────────────────────
    // Copied verbatim into every mod. It must not reference a single FruitLib type: it runs
    // precisely to find out whether FruitLib is there, and a typeref to a missing assembly
    // fails the whole method before the check can run. Change only the namespace.

    internal static class FruitGate
    {
        private const string LibName = "FruitLib";

        /// <summary>Why <see cref="Check"/> failed; used as the unregister reason.</summary>
        public static string FailureReason { get; private set; }

        public static bool Check(string modName, int major, int minor, int patch)
        {
            string required = major + "." + minor + "." + patch;

            var lib = FindLoaded(LibName);
            if (lib == null)
                return Fail(modName, required, "it is not installed");

            string found = ReadVersion(lib);
            if (found == null)
                return Fail(modName, required, "the installed copy is too old to report a version");

            if (!AtLeast(found, major, minor, patch))
                return Fail(modName, required, "found " + found);

            FailureReason = null;
            return true;
        }

        // ── Discovery ─────────────────────────────────────────────────────────

        private static Assembly FindLoaded(string simpleName)
        {
            Assembly[] all;
            try { all = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { return null; }

            foreach (var a in all)
            {
                try
                {
                    if (string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        return a;
                }
                catch { /* dynamic or half-loaded assembly — skip it */ }
            }
            return null;
        }

        // Reaches FruitVersion by name only, never as a typeref.
        private static string ReadVersion(Assembly lib)
        {
            try
            {
                var t = lib.GetType("FruitLib.FruitVersion", false);
                var p = t?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                if (p?.GetValue(null) is string s && s.Length > 0) return s;
            }
            catch { }

            try
            {
                var info = lib.GetCustomAttribute<MelonInfoAttribute>();
                if (!string.IsNullOrEmpty(info?.Version)) return info.Version;
            }
            catch { }

            try
            {
                var v = lib.GetName().Version;
                if (v != null) return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { }

            return null;
        }

        // ── Comparison ────────────────────────────────────────────────────────

        private static bool AtLeast(string found, int major, int minor, int patch)
        {
            var parts = found.Split('.');
            int fMajor = Segment(parts, 0), fMinor = Segment(parts, 1), fPatch = Segment(parts, 2);

            if (fMajor != major) return fMajor > major;
            if (fMinor != minor) return fMinor > minor;
            return fPatch >= patch;
        }

        private static int Segment(string[] parts, int i)
        {
            if (i >= parts.Length) return 0;

            string s = parts[i];
            int end = 0;
            while (end < s.Length && s[end] >= '0' && s[end] <= '9') end++;

            return end > 0 && int.TryParse(s.Substring(0, end), out int v) ? v : 0;
        }

        // ── Reporting ─────────────────────────────────────────────────────────

        private static bool Fail(string modName, string required, string detail)
        {
            FailureReason = "requires FruitLib " + required + " or newer — " + detail;
            MelonLogger.Error(
                $"[{modName}] Not starting: needs FruitLib {required} or newer, but {detail}. " +
                "Drop the latest FruitLib.dll into your Mods folder.");
            return false;
        }
    }
}
