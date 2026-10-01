using System;
using MelonLoader;

namespace FruitLib
{
    public static class FruitVersion
    {
        internal const string VersionConst = "5.6.0";

        public static string Current => VersionConst;

        public static readonly int Major;
        public static readonly int Minor;
        public static readonly int Patch;

        public const string MelonName   = "FruitLib";
        public const string MelonAuthor = "Luca_Nero";

        static FruitVersion() => (Major, Minor, Patch) = Parse(VersionConst);

        public static bool AtLeast(int major, int minor = 0, int patch = 0)
            => Compare((Major, Minor, Patch), (major, minor, patch)) >= 0;

        [Obsolete("Use the FruitGate from Templates/: it reaches FruitVersion by name only, so a missing or " +
                  "outdated FruitLib is reported instead of failing the mod's load with a TypeLoadException.")]
        public static bool Require(string modName, int major, int minor = 0, int patch = 0)
        {
            if (AtLeast(major, minor, patch)) return true;

            MelonLogger.Error(
                $"[FruitLib] '{modName}' requires FruitLib {major}.{minor}.{patch} or newer, " +
                $"but {Current} is installed '{modName}' will not start. " +
                $"Update FruitLib.dll in the Mods folder.");
            return false;
        }

        /// <summary>"1.2.3", "v1.2" or "1.2.3-beta+build" as (1, 2, 3). Missing or unreadable parts are 0.</summary>
        internal static (int major, int minor, int patch) Parse(string version)
        {
            if (string.IsNullOrEmpty(version)) return (0, 0, 0);

            string core = version.TrimStart('v', 'V');
            int cut = core.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) core = core.Substring(0, cut);

            var p = core.Split('.');
            int major = 0, minor = 0, patch = 0;
            if (p.Length > 0) int.TryParse(p[0], out major);
            if (p.Length > 1) int.TryParse(p[1], out minor);
            if (p.Length > 2) int.TryParse(p[2], out patch);
            return (major, minor, patch);
        }

        internal static int Compare((int major, int minor, int patch) a, (int major, int minor, int patch) b)
        {
            if (a.major != b.major) return a.major.CompareTo(b.major);
            if (a.minor != b.minor) return a.minor.CompareTo(b.minor);
            return a.patch.CompareTo(b.patch);
        }
    }
}
